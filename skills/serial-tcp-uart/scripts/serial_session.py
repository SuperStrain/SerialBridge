#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
serial_session.py — 通过 TCP 端口与 SerialBridge 串口设备交互的会话工具。

用途：
  Linux agent 通过本脚本（或 import 本模块）与远程串口设备程序化交互，
  自动处理：连接占用检测、登录流程、提示符等待、命令回显剥离、超时保护。

依赖：仅 Python 3.8+ 标准库。

CLI 用法：
  # 执行单条命令并返回输出（最适合 agent 脚本调用）
  python3 serial_session.py --host 192.168.1.100 --port 9001 exec "uname -a"

  # 需要登录的设备（先等 login:/Password: 再进入 shell）
  python3 serial_session.py --host 192.168.1.100 --port 9001 \
      --login root --password 123456 exec "ifconfig"

  # 自定义 shell 提示符（设备提示符非默认样式时必填，见 SKILL.md 说明）
  python3 serial_session.py --host 192.168.1.100 --port 9001 \
      --prompt '(~ # |# $)' exec "ls /"

  # 发送任意原始字节（十六进制），等待 2 秒并打印收到的数据
  python3 serial_session.py --host 192.168.1.100 --port 9001 raw --hex "4154 0d" --wait 2

库用法：
  from serial_session import SerialTcpSession
  s = SerialTcpSession("192.168.1.100", 9001, login="root", password="123456")
  s.connect()                      # False = 端口被其他客户端占用
  print(s.exec_command("uname -a"))
  s.close()
"""

import argparse
import re
import socket
import sys
import time

# 默认 shell 提示符：覆盖 "~ # "、"$ "、"# "、"[root@host ~]# " 等常见样式。
# 若设备提示符不匹配，用 --prompt 显式指定（正则，按行尾匹配）。
DEFAULT_PROMPT_RE = r"(?:^|\r?\n)[^\r\n]{0,40}(?:[\$#] |~ \$ |~ # )\Z"

LOGIN_RE = re.compile(rb"(?i)login\s*:\s*$")
PASSWORD_RE = re.compile(rb"(?i)password\s*:\s*$")


class SerialTcpError(Exception):
    pass


class PortBusyError(SerialTcpError):
    """端口被其他客户端占用（SerialBridge 单客户端模式拒绝新连接）。"""


class PromptTimeoutError(SerialTcpError):
    """在超时时间内未等到预期提示符/模式。"""


class SerialTcpSession:
    """一条 SerialBridge TCP 串口会话。线程不安全，单线程使用。"""

    def __init__(self, host: str, port: int,
                 login: str = None, password: str = None,
                 prompt_re: str = None,
                 connect_timeout: float = 5.0,
                 default_timeout: float = 8.0):
        self.host = host
        self.port = port
        self.login = login
        self.password = password
        self.prompt_re = re.compile(
            prompt_re.encode() if prompt_re else DEFAULT_PROMPT_RE.encode())
        self.connect_timeout = connect_timeout
        self.default_timeout = default_timeout
        self.sock: socket.socket = None

    # ---------- 连接管理 ----------

    def connect(self, attempts: int = 3, retry_delay: float = 0.6) -> bool:
        """
        建立会话。返回 True = 就绪；False = 端口被占用（单客户端模式拒绝新连接）。
        占用时 SerialBridge 会立即关闭新连接，表现为 connect 成功但随后立即 EOF。

        上一个客户端刚断开、名额尚未释放时会瞬时被拒，因此默认自动重试
        attempts 次（间隔 retry_delay）；主机不可达则不重试，直接抛错。
        """
        for i in range(max(1, attempts)):
            if i > 0:
                time.sleep(retry_delay)
            try:
                self.sock = socket.create_connection(
                    (self.host, self.port), timeout=self.connect_timeout)
            except OSError:
                raise SerialTcpError(
                    f"无法连接 {self.host}:{self.port}（主机不可达或映射未启动/端口错误）")

            self.sock.setblocking(False)

            # 占用探测：被拒绝的连接会在片刻内被服务端关闭（EOF）
            first = self._recv_for(0.4)
            if first is None:
                self.close()
                continue

            buf = first
            # 需要登录的设备：等待 login: 提示并完成登录
            if LOGIN_RE.search(buf):
                buf += self._send_and_wait((self.login or "root").encode() + b"\r",
                                           [PASSWORD_RE], 5.0)
                if PASSWORD_RE.search(buf):
                    buf += self._send_and_wait((self.password or "").encode() + b"\r",
                                               [self.prompt_re], 8.0)
            return True
        # 所有尝试均被占用
        return False

    def close(self):
        if self.sock:
            try:
                self.sock.close()
            except OSError:
                pass
            self.sock = None

    # ---------- 底层收发 ----------

    def _recv_for(self, duration: float) -> bytes:
        """在 duration 秒内读取所有到达数据；连接被对端关闭返回 None。"""
        buf = b""
        deadline = time.time() + duration
        while time.time() < deadline:
            try:
                chunk = self.sock.recv(4096)
            except socket.timeout:
                continue
            except BlockingIOError:
                time.sleep(0.02)
                continue
            if chunk == b"":
                return None
            buf += chunk
        return buf

    def _send_and_wait(self, data: bytes, patterns, timeout: float) -> bytes:
        self.sock.sendall(data)
        end = time.time() + timeout
        acc = b""
        while time.time() < end:
            chunk = self._recv_for(0.1)
            if chunk is None:
                raise SerialTcpError("连接被对端关闭")
            acc += chunk
            if any(p.search(acc) for p in patterns):
                return acc
        raise PromptTimeoutError(f"等待提示符超时（已收到 {acc[-200:]!r}）")

    def send_raw(self, data: bytes):
        """原样发送字节（Ctrl+C = b'\\x03'，回车 = b'\\r'）。"""
        if not self.sock:
            raise SerialTcpError("会话未建立")
        self.sock.sendall(data)

    def read_available(self, duration: float = 1.0) -> bytes:
        """读取 duration 秒内到达的所有数据（被动监听设备主动上报）。"""
        buf = self._recv_for(duration)
        return buf or b""

    # ---------- 命令执行 ----------

    def exec_command(self, cmd: str, timeout: float = None,
                     prompt_re: str = None) -> str:
        """
        执行一条 shell 命令并返回其输出（已剥离命令回显与提示符）。
        串口是字节流：必须以提示符作为完成标志，不能用固定时长判断。
        """
        if not self.sock:
            raise SerialTcpError("会话未建立")
        pat = re.compile(prompt_re.encode()) if prompt_re else self.prompt_re
        t = timeout or self.default_timeout

        self._recv_for(0.15)                      # 丢弃上次残留
        self.sock.sendall(cmd.encode() + b"\r")   # 串口终端行结束用 \r
        end = time.time() + t
        acc = b""
        while time.time() < end:
            chunk = self._recv_for(0.1)
            if chunk is None:
                raise SerialTcpError("连接被对端关闭")
            acc += chunk
            # 提示符必须出现在"输出尾部"才表示命令结束（避免命令输出中碰巧含 # 误判）
            if pat.search(acc[-120:]):
                break
        else:
            raise PromptTimeoutError(
                f"命令 {cmd!r} 在 {t}s 内未回到提示符（设备可能仍在输出，可加大超时）")

        text = acc.decode("utf-8", "replace")
        lines = text.splitlines()
        # 剥离命令回显行（第一个非空行），前导空行一并去除
        for i, ln in enumerate(lines):
            if ln.strip():
                if cmd.split()[0] in ln:
                    lines = lines[i + 1:]
                break
        # 剥离尾部提示符行
        if lines and pat.search(lines[-1].encode(errors="replace")):
            lines = lines[:-1]
        return "\n".join(lines).strip()

    def interrupt(self):
        """发送 Ctrl+C 清空设备当前输入行。"""
        self.send_raw(b"\x03")
        time.sleep(0.1)
        self._recv_for(0.2)


# ---------- CLI ----------

def main(argv):
    ap = argparse.ArgumentParser(description="SerialBridge TCP 串口会话工具")
    ap.add_argument("--host", required=True)
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--login", default=None)
    ap.add_argument("--password", default=None)
    ap.add_argument("--prompt", dest="prompt_re", default=None,
                    help="设备提示符正则（默认覆盖常见的 # / $ / ~ # 样式）")
    ap.add_argument("--timeout", type=float, default=8.0)
    sub = ap.add_subparsers(dest="cmd", required=True)

    p_exec = sub.add_parser("exec", help="执行一条 shell 命令")
    p_exec.add_argument("command")

    p_raw = sub.add_parser("raw", help="发送原始字节并监听响应")
    p_raw.add_argument("--hex", required=True, help="十六进制字节，空格分隔，如 '4154 0d'")
    p_raw.add_argument("--wait", type=float, default=2.0, help="监听时长（秒）")

    args = ap.parse_args(argv)

    try:
        s = SerialTcpSession(args.host, args.port, login=args.login,
                             password=args.password, prompt_re=args.prompt_re,
                             default_timeout=args.timeout)
        if not s.connect():
            print("端口被占用：已有其他客户端连接（单客户端模式）。"
                  "请先让对端断开，或在映射配置中改用多客户端模式。", file=sys.stderr)
            return 3
    except SerialTcpError as e:
        print(f"连接失败：{e}", file=sys.stderr)
        return 2

    try:
        if args.cmd == "exec":
            print(s.exec_command(args.command, timeout=args.timeout))
        elif args.cmd == "raw":
            s.send_raw(bytes.fromhex(args.hex.replace(" ", "")))
            data = s.read_available(args.wait)
            print(data.hex(" "))
            sys.stdout.write(data.decode("utf-8", "replace"))
        return 0
    except PromptTimeoutError as e:
        print(f"超时：{e}", file=sys.stderr)
        return 4
    except SerialTcpError as e:
        print(f"错误：{e}", file=sys.stderr)
        return 2
    finally:
        s.close()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
