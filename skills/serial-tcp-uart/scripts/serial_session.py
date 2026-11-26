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
import os
import re
import secrets
import select
import socket
import sys
import time
from dataclasses import dataclass

# 默认 shell 提示符：覆盖 "~ # "、"$ "、"# "、"[root@host ~]# " 等常见样式。
# 若设备提示符不匹配，用 --prompt 显式指定（正则，按行尾匹配）。
DEFAULT_PROMPT_RE = r"(?:^|\r?\n)(?:\[[^\]\r\n]{1,40}\]|\([^\)\r\n]{1,40}\)|[A-Za-z0-9_.-]+@[A-Za-z0-9_.-]+(?:[^\r\n ]*)?|[A-Za-z0-9_.-]+)?[ \t]?(?:~[ \t]?)?[\$#] (?=(?:\r?\n|$|(?:WRN|ERR|INF|DBG|INFO|WARN|ERROR|NOTICE|DEBUG|CRIT|ALERT|EMERG):))"

LOGIN_RE = re.compile(rb"(?i)login\s*:\s*$")
PASSWORD_RE = re.compile(rb"(?i)password\s*:\s*$")


class SerialTcpError(Exception):
    pass


class PortBusyError(SerialTcpError):
    """端口被其他客户端占用（SerialBridge 单客户端模式拒绝新连接）。"""


class PromptTimeoutError(SerialTcpError):
    """在超时时间内未等到预期提示符/模式。"""


class CommandSendError(SerialTcpError):
    """命令发送阶段失败；部分字节可能已经到达设备，不能自动重发。"""

    command_may_have_executed = True


class CommandCompletionTimeoutError(PromptTimeoutError):
    """Shell 命令可能已经执行，但没有观察到可信的结束标记。"""

    command_may_have_executed = True

    def __init__(self, message, raw_tail=b"", capture_path=None):
        super().__init__(message)
        self.raw_tail = raw_tail
        self.capture_path = capture_path


class CommandCaptureError(SerialTcpError):
    """本地日志文件写入失败；命令是否完成由 command_completed 标识。"""

    command_may_have_executed = True

    def __init__(self, message, command_completed=False, capture_path=None):
        super().__init__(message)
        self.command_completed = command_completed
        self.capture_path = capture_path


@dataclass(frozen=True)
class CommandResult:
    """命令完成结果。exit_code=None 表示使用提示符模式，未获取 Shell 退出码。"""

    output: str
    exit_code: int = None
    completion: str = "prompt"
    output_truncated: bool = False
    capture_path: str = None


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
        self._pending_truncated = False
        self._eof = False
        self._command_state_uncertain = False
        self._last_wait_truncated = False
        self.last_read_bytes = 0
        self.last_read_truncated = False
        self.last_read_capture_path = None

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
                first = self._recv_for(0.4, max_bytes=65536)
                if first is None:
                    self.close()
                    continue
                self._store_pending(first)
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
        try:
            initial = self._wait_for([LOGIN_RE, self.prompt_re], min(0.5, self.default_timeout))
        except PromptTimeoutError:
            initial = self._send_and_wait(b"\r", [LOGIN_RE, self.prompt_re],
                                          self.default_timeout)
        if self.prompt_re.search(initial):
            self._store_pending(initial + bytes(self._pending), self._pending_truncated)
            return
        response = self._send_and_wait(
            self.login.encode("utf-8") + b"\r",
            [PASSWORD_RE, self.prompt_re, LOGIN_RE], self.default_timeout)
        if LOGIN_RE.search(response):
            raise SerialTcpError("设备拒绝用户名")
        if self.prompt_re.search(response):
            return  # Passwordless login
        if self.password is None:
            raise SerialTcpError("设备需要密码，请提供 --password")
        result = self._send_and_wait(self.password.encode("utf-8") + b"\r",
                                     [self.prompt_re, LOGIN_RE, PASSWORD_RE],
                                     self.default_timeout)
        if not self.prompt_re.search(result):
            raise SerialTcpError("设备未进入 Shell，登录可能失败")

    def close(self):
        if self.sock:
            try:
                self.sock.close()
            except OSError:
                pass
        self.sock = None
        self._pending.clear()
        self._pending_truncated = False
        self._eof = False
        self._command_state_uncertain = False

    def _store_pending(self, data: bytes, was_truncated: bool = False):
        """保留待读数据，同时限制异步输出缓存最多 1 MiB。"""
        pending = bytearray(data)
        truncated = was_truncated
        limit = 1024 * 1024
        if len(pending) > limit:
            head_size = limit // 2
            tail_size = limit - head_size
            pending = pending[:head_size] + pending[-tail_size:]
            truncated = True
        self._pending = pending
        self._pending_truncated = truncated

    # ---------- 底层收发 ----------

    def _recv_for(self, duration: float, max_bytes: int = None):
        """Read for duration seconds; None means EOF. Does not consume _pending.

        max_bytes bounds one read batch without discarding unread socket data.
        """
        if not self.sock:
            raise SerialTcpError("会话未建立")
        if self._eof:
            raise SerialTcpError("串口 TCP 连接已关闭")
        buf = bytearray()
        end = time.monotonic() + max(0, duration)
        while True:
            remaining = end - time.monotonic()
            if remaining <= 0 or (max_bytes is not None and len(buf) >= max_bytes):
                break
            try:
                ready, _, _ = select.select([self.sock], [], [], remaining)
                if not ready:
                    break
                read_size = 4096 if max_bytes is None else min(4096, max_bytes - len(buf))
                chunk = self.sock.recv(read_size)
                if not chunk:
                    self._eof = True
                    return bytes(buf) if buf else None
                buf.extend(chunk)
            except BlockingIOError:
                continue
            except OSError as exc:
                raise SerialTcpError(f"读取串口连接失败: {exc}") from exc
        return bytes(buf)

    def _wait_for(self, patterns, timeout: float) -> bytes:
        acc = bytearray(self._pending)
        pending_truncated = self._pending_truncated
        self._pending.clear()
        self._pending_truncated = False
        self._last_wait_truncated = pending_truncated
        if len(acc) > 1024 * 1024:
            del acc[:-1024 * 1024]
            self._last_wait_truncated = True
        end = time.monotonic() + timeout
        while True:
            for pat in patterns:
                match = pat.search(acc)
                if match:
                    if match.end() < len(acc):
                        self._store_pending(bytes(acc[match.end():]))
                    return bytes(acc[:match.end()])
            remaining = end - time.monotonic()
            if remaining <= 0:
                self._store_pending(bytes(acc), self._last_wait_truncated)
                raise PromptTimeoutError(f"等待提示符超时（已收到 {bytes(acc[-200:])!r}）")
            chunk = self._recv_for(min(remaining, 0.15), max_bytes=65536)
            if chunk is None:
                self._store_pending(bytes(acc), self._last_wait_truncated)
                raise SerialTcpError("连接被对端关闭")
            acc.extend(chunk)
            if len(acc) > 1024 * 1024:
                del acc[:-1024 * 1024]
                self._last_wait_truncated = True
            if self._eof and not any(p.search(acc) for p in patterns):
                self._store_pending(bytes(acc), self._last_wait_truncated)
                raise SerialTcpError("连接被对端关闭")

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

    def read_available(self, duration: float = 1.0,
                        max_bytes: int = 1024 * 1024,
                        capture_path: str = None) -> bytes:
        """在限定时长内采集异步串口输出。

        返回值最多保留 max_bytes 的首尾预览；完整数据可通过 capture_path
        流式写入文件。用 last_read_truncated/last_read_bytes 查看截断情况。
        """
        if max_bytes < 0:
            raise ValueError("max_bytes 不能小于 0")
        path_text = os.fspath(capture_path) if capture_path is not None else None
        file_handle = open(path_text, "wb") if path_text is not None else None
        buffered = bytes(self._pending)
        pending_truncated = self._pending_truncated
        self._pending.clear()
        self._pending_truncated = False
        head_limit = (max_bytes + 1) // 2
        tail_limit = max_bytes - head_limit
        head = bytearray()
        tail = bytearray()
        total = 0

        def capture(data):
            nonlocal total
            if not data:
                return
            total += len(data)
            if file_handle is not None:
                file_handle.write(data)
            remaining = head_limit - len(head)
            if remaining > 0:
                head.extend(data[:remaining])
                data = data[remaining:]
            if tail_limit and data:
                tail.extend(data)
                if len(tail) > tail_limit:
                    del tail[:len(tail) - tail_limit]

        try:
            capture(buffered)
            deadline = time.monotonic() + max(0, duration)
            eof = False
            while time.monotonic() < deadline:
                try:
                    incoming = self._recv_for(
                        min(deadline - time.monotonic(), 0.15), max_bytes=65536
                    )
                except SerialTcpError:
                    if self._eof:
                        eof = True
                        break
                    raise
                if incoming is None:
                    eof = True
                    break
                capture(incoming)
            self.last_read_bytes = total
            self.last_read_truncated = total > max_bytes or pending_truncated
            self.last_read_capture_path = path_text
            if file_handle is not None:
                file_handle.flush()
            if eof and total == 0:
                raise SerialTcpError("串口 TCP 连接已关闭")
            return bytes(head) + bytes(tail)
        finally:
            if file_handle is not None:
                file_handle.close()

    # ---------- 命令执行 ----------

    def exec_command(self, cmd: str, timeout: float = None,
                     prompt_re: str = None, completion: str = "prompt",
                     max_capture_bytes: int = 1024 * 1024,
                     capture_path: str = None) -> str:
        """兼容接口：执行命令并返回输出。需要退出码时使用 exec_command_result()。"""
        return self.exec_command_result(
            cmd, timeout=timeout, prompt_re=prompt_re, completion=completion,
            max_capture_bytes=max_capture_bytes, capture_path=capture_path
        ).output

    def exec_command_result(self, cmd: str, timeout: float = None,
                            prompt_re: str = None, completion: str = "prompt",
                            max_capture_bytes: int = 1024 * 1024,
                            capture_path: str = None) -> CommandResult:
        """执行命令并返回结构化结果。

        completion="prompt" 保持旧设备/CLI 的提示符协议。
        completion="shell" 仅用于已确认的 POSIX/Linux Shell，通过随机标记
        识别命令边界并获取退出码，不依赖提示符或 TCP recv 分块边界。
        """
        if not self.sock:
            raise SerialTcpError("会话未建立")
        if self._command_state_uncertain:
            raise SerialTcpError(
                "上一条命令的发送/完成状态未知；为避免重复执行，已禁止继续发送。"
                "请重新连接并人工确认设备状态后再操作。"
            )
        if completion == "shell":
            return self._exec_shell_command(
                cmd, timeout=timeout, max_capture_bytes=max_capture_bytes,
                capture_path=capture_path
            )
        if completion != "prompt":
            raise ValueError("completion 必须是 'prompt' 或 'shell'")
        if capture_path is not None:
            raise ValueError("capture_path 目前仅支持 completion='shell'")
        return self._exec_prompt_command(cmd, timeout=timeout, prompt_re=prompt_re)

    def _exec_prompt_command(self, cmd: str, timeout: float = None,
                             prompt_re: str = None) -> CommandResult:
        """旧提示符模式，保留给 U-Boot、MCU CLI 及未确认的 Shell 环境。"""
        pat = re.compile(prompt_re.encode()) if prompt_re else self.prompt_re
        t = timeout or self.default_timeout
        previous = bytes(self._pending)
        previous_truncated = self._pending_truncated
        self._pending.clear()
        self._pending_truncated = False
        try:
            self.send_raw(cmd.encode() + b"\r")
        except SerialTcpError as exc:
            self._command_state_uncertain = True
            self._store_pending(previous, previous_truncated)
            raise CommandSendError(
                "命令发送阶段失败，可能已有部分字节到达设备；无法确认是否执行，禁止自动重发。"
            ) from exc
        try:
            acc = self._wait_for([pat], t)
        except PromptTimeoutError as exc:
            raw = bytes(self._pending[-8192:])
            self._command_state_uncertain = True
            self._store_pending(previous + bytes(self._pending),
                                previous_truncated or self._pending_truncated)
            raise CommandCompletionTimeoutError(
                "命令已发送，但未观察到提示符；命令可能已经执行，不要直接重发。"
                "请保留输出并重新同步设备状态。",
                raw_tail=raw
            ) from exc
        except SerialTcpError as exc:
            raw = bytes(self._pending[-8192:])
            self._command_state_uncertain = True
            self._store_pending(previous + bytes(self._pending),
                                previous_truncated or self._pending_truncated)
            raise CommandCompletionTimeoutError(
                "命令已发送，但读取连接时发生错误，未能确认命令完成；命令可能已经执行，"
                "不要直接重发。请重新连接并人工确认。",
                raw_tail=raw
            ) from exc
        if previous or self._pending_truncated:
            self._store_pending(previous + bytes(self._pending),
                                previous_truncated or self._pending_truncated)

        text = acc.decode("utf-8", "replace")
        lines = text.splitlines()
        for i, ln in enumerate(lines):
            if ln.strip():
                if cmd.split() and cmd.split()[0] in ln:
                    lines = lines[i + 1:]
                break
        if lines and pat.search(lines[-1].encode(errors="replace")):
            last = lines[-1]
            tail = re.sub(r"(?:~ )?[#$] $", "", last)
            if tail == last or (tail.startswith("[") and tail.endswith("]")):
                lines = lines[:-1]
            elif tail:
                lines[-1] = tail
            else:
                lines = lines[:-1]
        return CommandResult("\n".join(lines).strip(), completion="prompt",
                             output_truncated=self._last_wait_truncated)

    def _exec_shell_command(self, cmd: str, timeout: float = None,
                            max_capture_bytes: int = 1024 * 1024,
                            capture_path: str = None) -> CommandResult:
        """POSIX Shell 专用的随机标记协议；持续日志不会影响结束标记识别。"""
        if "\x00" in cmd:
            raise ValueError("Shell 命令不能包含 NUL 字节")
        if max_capture_bytes < 0:
            raise ValueError("max_capture_bytes 不能小于 0")
        t = timeout or self.default_timeout
        token = secrets.token_hex(16)
        begin = "__SERIAL_SESSION_" + token + "_BEGIN__"
        end = "__SERIAL_SESSION_" + token + "_END__"
        rc_var = "__serial_session_rc_" + token
        # Braces preserve shell state (for example, cd); the newline before }
        # also prevents a trailing shell comment in cmd from swallowing the wrapper.
        # Keep the entire protocol in one outer compound command. Interactive
        # shells may emit PS1/PS2 between top-level commands; those prompts must
        # not be mistaken for command output between the markers.
        wrapper = (
            "{\n"
            "printf '\\n%s\\n' '" + begin + "'\n"
            "{\n" + cmd + "\n}\n"
            + rc_var + "=$?\n"
            + "printf '\\n%s:%s\\n' '" + end + "' \"$" + rc_var + "\"\n"
            + "}\n"
        )
        begin_re = re.compile(
            rb"(?:^|\r?\n)" + re.escape(begin.encode("ascii")) + rb"\r?\n"
        )
        end_re = re.compile(
            rb"(?:^|\r?\n)" + re.escape(end.encode("ascii")) + rb":([0-9]{1,3})\r?\n"
        )
        keep_before = len(begin) + 8
        keep_after = len(end) + 12
        head_limit = (max_capture_bytes + 1) // 2
        tail_limit = max_capture_bytes - head_limit
        head = bytearray()
        tail = bytearray()
        total_output = 0
        buffer = bytearray()
        prelude = bytearray()
        prelude_truncated = False
        started = False
        path_text = os.fspath(capture_path) if capture_path is not None else None
        # Open before touching session state or sending, so a bad path is harmless.
        try:
            file_handle = open(path_text, "wb") if path_text is not None else None
        except OSError as exc:
            raise SerialTcpError(f"无法创建串口采集文件，命令尚未发送: {exc}") from exc
        previous = bytes(self._pending)
        previous_truncated = self._pending_truncated
        self._pending.clear()
        self._pending_truncated = False

        def capture(data: bytes):
            nonlocal total_output
            if not data:
                return
            total_output += len(data)
            if file_handle is not None:
                file_handle.write(data)
            remaining = head_limit - len(head)
            if remaining > 0:
                head.extend(data[:remaining])
                data = data[remaining:]
            if tail_limit and data:
                tail.extend(data)
                if len(tail) > tail_limit:
                    del tail[:len(tail) - tail_limit]

        def keep_prelude(data: bytes):
            nonlocal prelude_truncated
            if not data:
                return
            prelude.extend(data)
            limit = 1024 * 1024
            if len(prelude) > limit:
                head_size = limit // 2
                tail_size = limit - head_size
                prelude[:] = prelude[:head_size] + prelude[-tail_size:]
                prelude_truncated = True

        def make_preview():
            truncated = total_output > max_capture_bytes
            if truncated:
                omitted = total_output - len(head) - len(tail)
                destination = ("完整内容见 " + path_text) if path_text else "完整内容未保存"
                raw = bytes(head) + (
                    "\n...[输出已截断，省略 " + str(omitted) + " 字节；" + destination + "]...\n"
                ).encode("utf-8") + bytes(tail)
            else:
                raw = bytes(head) + bytes(tail)
            return raw.decode("utf-8", "replace").strip(), truncated

        command_completed = False
        try:
            try:
                self.send_raw(wrapper.encode("utf-8"))
            except SerialTcpError as exc:
                self._command_state_uncertain = True
                self._store_pending(previous, previous_truncated)
                raise CommandSendError(
                    "Shell 命令发送阶段失败，可能已有部分字节到达设备；"
                    "无法确认命令是否执行，禁止自动重发。请重新连接并人工确认。"
                ) from exc

            deadline = time.monotonic() + t
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raw_tail = (bytes(tail) + bytes(buffer))[-8192:]
                    if started:
                        capture(bytes(buffer))
                        buffer.clear()
                    self._command_state_uncertain = True
                    self._store_pending(previous + bytes(prelude) + raw_tail[-65536:],
                                        previous_truncated or prelude_truncated)
                    preview, _ = make_preview()
                    detail = ("；输出预览：" + preview[-500:]) if preview else ""
                    raise CommandCompletionTimeoutError(
                        "已发送 Shell 命令，但未收到可信的结束标记；命令可能已经执行，"
                        "不要直接重发。请检查原始输出/设备状态并重新同步。" + detail,
                        raw_tail=raw_tail, capture_path=path_text
                    )

                try:
                    chunk = self._recv_for(min(remaining, 0.15), max_bytes=65536)
                except SerialTcpError as exc:
                    if self._eof:
                        chunk = None
                    else:
                        raw_tail = (bytes(tail) + bytes(buffer))[-8192:]
                        if started:
                            capture(bytes(buffer))
                        self._command_state_uncertain = True
                        self._store_pending(previous + bytes(prelude) + raw_tail[-65536:],
                                            previous_truncated or prelude_truncated)
                        raise CommandCompletionTimeoutError(
                            "等待 Shell 命令结束标记时读取连接失败；命令可能已经执行，"
                            "不要自动重发。请重新连接并人工确认。",
                            raw_tail=raw_tail, capture_path=path_text
                        ) from exc
                if chunk is None:
                    raw_tail = (bytes(prelude) + bytes(tail) + bytes(buffer))[-8192:]
                    if started:
                        capture(bytes(buffer))
                        buffer.clear()
                        pending_output = bytes(tail)
                    else:
                        pending_output = bytes(buffer)
                    self._command_state_uncertain = True
                    self._store_pending(
                        previous + bytes(prelude) + pending_output,
                        previous_truncated or prelude_truncated
                    )
                    raise CommandCompletionTimeoutError(
                        "等待 Shell 命令结束标记时 TCP 连接关闭；命令可能已经执行，"
                        "不要自动重发。请重新连接并人工确认。",
                        raw_tail=raw_tail, capture_path=path_text
                    )
                if not chunk:
                    continue
                buffer.extend(chunk)

                if not started:
                    match = begin_re.search(buffer)
                    if match:
                        keep_prelude(bytes(buffer[:match.start()]))
                        started = True
                        del buffer[:match.end()]
                    else:
                        if len(buffer) > keep_before:
                            cut = len(buffer) - keep_before
                            keep_prelude(bytes(buffer[:cut]))
                            del buffer[:cut]
                        continue

                match = end_re.search(buffer)
                if match:
                    exit_code = int(match.group(1))
                    remainder = bytes(buffer[match.end():])
                    self._store_pending(
                        previous + bytes(prelude) + remainder,
                        previous_truncated or prelude_truncated
                    )
                    command_completed = True
                    self._command_state_uncertain = False
                    capture(bytes(buffer[:match.start()]))
                    buffer.clear()
                    if file_handle is not None:
                        file_handle.flush()
                    preview, truncated = make_preview()
                    return CommandResult(
                        output=preview,
                        exit_code=exit_code,
                        completion="shell",
                        output_truncated=truncated,
                        capture_path=path_text
                    )

                # Keep only enough suffix to recognize an end marker split across
                # arbitrary TCP chunks; all older command output is streamed out.
                safe_length = len(buffer) - keep_after
                if safe_length > 0:
                    capture(bytes(buffer[:safe_length]))
                    del buffer[:safe_length]
        except OSError as exc:
            if command_completed:
                self._command_state_uncertain = False
                raise CommandCaptureError(
                    "Shell 命令已收到结束标记，但本地采集文件写入/刷新失败；"
                    "命令已完成，日志文件可能不完整。",
                    command_completed=True, capture_path=path_text
                ) from exc
            self._command_state_uncertain = True
            raw_tail = (bytes(tail) + bytes(buffer))[-8192:]
            self._store_pending(
                previous + bytes(prelude) + raw_tail,
                previous_truncated or prelude_truncated
            )
            raise CommandCaptureError(
                "本地采集文件写入失败，Shell 命令可能已经执行但完成状态未知；"
                "当前会话已锁定，请勿重发命令。",
                command_completed=False, capture_path=path_text
            ) from exc
        finally:
            if file_handle is not None:
                active_exception = sys.exc_info()[0] is not None
                try:
                    file_handle.close()
                except OSError as exc:
                    if not active_exception:
                        self._command_state_uncertain = not command_completed
                        raise CommandCaptureError(
                            "关闭本地采集文件失败；" +
                            ("Shell 命令已完成，但日志文件可能不完整。" if command_completed
                             else "Shell 命令可能已经执行，完成状态未知。"),
                            command_completed=command_completed, capture_path=path_text
                        ) from exc

    def interrupt(self):
        """发送 Ctrl+C 清空设备当前输入行。"""
        self.send_raw(b"\x03")
        time.sleep(0.1)
        response = self._recv_for(0.2, max_bytes=65536)
        if response:
            self._store_pending(bytes(self._pending) + response, self._pending_truncated)


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
    ap.add_argument("--completion", choices=("prompt", "shell"), default="prompt",
                    help="命令完成协议：prompt 兼容通用 CLI；shell 仅用于已确认的 POSIX/Linux Shell")
    ap.add_argument("--capture", default=None,
                    help="shell 模式下将完整命令输出流式写入本地文件")
    ap.add_argument("--max-output-bytes", type=int, default=1024 * 1024,
                    help="shell 模式返回给调用方的最大内存预览字节数（默认 1 MiB）")
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
            result = s.exec_command_result(
                args.command, timeout=args.timeout, completion=args.completion,
                max_capture_bytes=args.max_output_bytes, capture_path=args.capture
            )
            print(result.output)
            if result.completion == "shell":
                print(f"命令退出码: {result.exit_code}", file=sys.stderr)
                if result.output_truncated:
                    print(f"输出已截断；完整输出: {result.capture_path or '未保存文件'}", file=sys.stderr)
        elif args.cmd == "raw":
            s.send_raw(bytes.fromhex(args.hex.replace(" ", "")))
            data = s.read_available(
                args.wait, max_bytes=args.max_output_bytes, capture_path=args.capture
            )
            if s.last_read_truncated:
                destination = s.last_read_capture_path or "未保存文件"
                print(
                    f"注意：以下仅为首尾预览，当前读取 {s.last_read_bytes} 字节；"
                    f"采集文件: {destination}。若此前缓存已截断，已丢失部分无法恢复。",
                    file=sys.stderr
                )
                split_at = (args.max_output_bytes + 1) // 2
                head, tail = data[:split_at], data[split_at:]
                print("[首部预览]")
                print(head.hex(" "))
                sys.stdout.write(head.decode("utf-8", "replace"))
                print("\n...[中间数据已省略，不能将此预览视为完整字节流]...\n")
                print("[尾部预览]")
                print(tail.hex(" "))
                sys.stdout.write(tail.decode("utf-8", "replace"))
            else:
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
