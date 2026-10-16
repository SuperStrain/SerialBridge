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
import select
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
        self._pending = bytearray()

    # ---------- 连接管理 ----------

    def connect(self, attempts: int = 3, retry_delay: float = 0.6) -> bool:
        """Connect to SerialBridge. False indicates an immediate peer close.

        Silence is not evidence of an occupied port. A shell or login prompt
        may arrive later; it is handled when explicitly configured.
        """
        self.close()
        for i in range(max(1, attempts)):
            if i:
                time.sleep(retry_delay)
            try:
                self.sock = socket.create_connection(
                    (self.host, self.port), timeout=self.connect_timeout)
                self.sock.setblocking(False)
                first = self._recv_for(0.4)
                if first is None:
                    self.close()
                    continue
                self._pending.extend(first)
                if self.login is not None:
                    self._login()
                return True
            except (SerialTcpError, PromptTimeoutError):
                self.close()
                raise
            except OSError as exc:
                self.close()
                raise SerialTcpError(
                    f"无法连接或初始化 {self.host}:{self.port}: {exc}") from exc
        return False

    def _login(self):
        """Explicit login: do not claim readiness until a shell prompt arrives."""
        initial = self._wait_for([LOGIN_RE, self.prompt_re], self.default_timeout)
        if self.prompt_re.search(initial):
            self._pending.extend(initial)
            return
        response = self._send_and_wait(
            self.login.encode("utf-8") + b"\\r",
            [PASSWORD_RE, self.prompt_re, LOGIN_RE], self.default_timeout)
        if LOGIN_RE.search(response):
            raise SerialTcpError("设备拒绝用户名")
        if self.prompt_re.search(response):
            return  # Passwordless login
        if self.password is None:
            raise SerialTcpError("设备需要密码，请提供 --password")
        self._send_and_wait(self.password.encode("utf-8") + b"\\r",
                            [self.prompt_re, LOGIN_RE, PASSWORD_RE], self.default_timeout)
        # Confirm that the login actually finished, rather than accepting a retry prompt.
        # _send_and_wait also preserves trailing bytes for the next read.

    def close(self):
        if self.sock:
            try:
                self.sock.close()
            except OSError:
                pass
        self.sock = None
        self._pending.clear()

    # ---------- 底层收发 ----------

    def _recv_for(self, duration: float):
        """Read for duration seconds; None means EOF. Does not consume _pending."""
        if not self.sock:
            raise SerialTcpError("会话未建立")
        buf = bytearray()
        end = time.monotonic() + max(0, duration)
        while True:
            remaining = end - time.monotonic()
            if remaining <= 0:
                break
            try:
                ready, _, _ = select.select([self.sock], [], [], remaining)
                if not ready:
                    break
                chunk = self.sock.recv(4096)
                if not chunk:
                    if buf:
                        self._pending.extend(buf)
                    return None
                buf.extend(chunk)
            except BlockingIOError:
                continue
            except OSError as exc:
                raise SerialTcpError(f"读取串口连接失败: {exc}") from exc
        return bytes(buf)

    def _wait_for(self, patterns, timeout: float) -> bytes:
        acc = bytearray(self._pending)
        self._pending.clear()
        end = time.monotonic() + timeout
        while True:
            for pat in patterns:
                match = pat.search(acc)
                if match:
                    if match.end() < len(acc):
                        self._pending.extend(acc[match.end():])
                    return bytes(acc[:match.end()])
            remaining = end - time.monotonic()
            if remaining <= 0:
                self._pending[:0] = acc
                raise PromptTimeoutError(f"等待提示符超时（已收到 {bytes(acc[-200:])!r}）")
            chunk = self._recv_for(min(remaining, 0.15))
            if chunk is None:
                self._pending[:0] = acc
                raise SerialTcpError("连接被对端关闭")
            acc.extend(chunk)

    def _send_and_wait(self, data: bytes, patterns, timeout: float) -> bytes:
        self.send_raw(data)
        return self._wait_for(patterns, timeout)

    def send_raw(self, data: bytes):
        """Send raw bytes with timeout and proper handling of partial writes."""
        if not self.sock:
            raise SerialTcpError("会话未建立")
        view = memoryview(data)
        deadline = time.monotonic() + self.default_timeout
        try:
            while view:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise PromptTimeoutError("发送串口数据超时")
                _, writable, _ = select.select([], [self.sock], [], remaining)
                if not writable:
                    raise PromptTimeoutError("发送串口数据超时")
                sent = self.sock.send(view)
                if sent == 0:
                    raise SerialTcpError("连接已关闭，无法发送")
                view = view[sent:]
        except OSError as exc:
            raise SerialTcpError(f"发送串口数据失败: {exc}") from exc

    def read_available(self, duration: float = 1.0) -> bytes:
        buffered = bytes(self._pending)
        self._pending.clear()
        incoming = self._recv_for(duration)
        if incoming is None:
            # The data already read before EOF remains available to callers.
            buffered += bytes(self._pending)
            self._pending.clear()
            return buffered
        return buffered + incoming

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

        # Preserve unsolicited output rather than discarding it silently.
        # It remains accessible through read_available() if a command fails.
        previous = bytes(self._pending)
        self._pending.clear()
        try:
            self.send_raw(cmd.encode() + b"\\r")
            acc = self._wait_for([pat], t)
        except (SerialTcpError, PromptTimeoutError):
            self._pending[:0] = previous
            raise
        # Previous asynchronous output is not part of this command response.
        if previous:
            self._pending[:0] = previous

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
        response = self._recv_for(0.2)
        if response:
            self._pending.extend(response)


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
    except PromptTimeoutError as e:
        print(f"登录或连接等待超时：{e}", file=sys.stderr)
        return 4
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
    except ValueError as e:
        print(f"无效输入：{e}", file=sys.stderr)
        return 2
    finally:
        s.close()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
