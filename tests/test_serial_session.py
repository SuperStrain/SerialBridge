import os
import re
import socket
import tempfile
import threading
import time
import unittest
from unittest import mock

import runpy
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "skills" / "serial-tcp-uart" / "scripts" / "serial_session.py"
module = runpy.run_path(str(MODULE_PATH))
SerialTcpSession = module["SerialTcpSession"]
PromptTimeoutError = module["PromptTimeoutError"]
CommandCompletionTimeoutError = module["CommandCompletionTimeoutError"]
CommandCaptureError = module["CommandCaptureError"]
SerialTcpError = module["SerialTcpError"]
DEFAULT_PROMPT_RE = module["DEFAULT_PROMPT_RE"]


class FakeShellServer:
    def __init__(self, response, fragment_size=0):
        self.response = response
        self.fragment_size = fragment_size
        self.requests = []
        self.errors = []
        self._listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._listener.bind(("127.0.0.1", 0))
        self._listener.listen(1)
        self._listener.settimeout(2)
        self.port = self._listener.getsockname()[1]
        self._thread = threading.Thread(target=self._serve, daemon=True)

    def __enter__(self):
        self._thread.start()
        return self

    def __exit__(self, *_):
        try:
            self._listener.close()
        except OSError:
            pass
        self._thread.join(timeout=2)

    def _send(self, conn, data):
        if not self.fragment_size:
            conn.sendall(data)
            return
        for offset in range(0, len(data), self.fragment_size):
            conn.sendall(data[offset:offset + self.fragment_size])
            time.sleep(0.0002)

    def _serve(self):
        try:
            conn, _ = self._listener.accept()
            with conn:
                conn.settimeout(2)
                self._send(conn, b"root@NVTEVM:~$ ")
                request = bytearray()
                while True:
                    chunk = conn.recv(4096)
                    if not chunk:
                        return
                    request.extend(chunk)
                    if b"_END__" in request and request.endswith(b"}\n"):
                        break
                raw = bytes(request)
                self.requests.append(raw)
                match = re.search(rb"(__SERIAL_SESSION_[0-9a-f]{32}_BEGIN__)", raw)
                if not match:
                    raise AssertionError("Shell begin marker was not sent")
                begin = match.group(1)
                end = begin.replace(b"_BEGIN__", b"_END__")
                self.response(conn, begin, end, self._send, raw)
        except (OSError, AssertionError) as exc:
            self.errors.append(exc)


class SerialSessionShellCompletionTests(unittest.TestCase):
    def run_session(self, response, command="uname -a", fragment_size=0,
                    timeout=2, **kwargs):
        with FakeShellServer(response, fragment_size=fragment_size) as server:
            session = SerialTcpSession("127.0.0.1", server.port, default_timeout=timeout)
            self.assertTrue(session.connect())
            try:
                result = session.exec_command_result(
                    command, completion="shell", timeout=timeout, **kwargs
                )
            finally:
                session.close()
            self.assertFalse(server.errors)
            return result, server

    def test_reported_prompt_followed_by_async_log_does_not_block_shell_completion(self):
        def response(conn, begin, end, send, _request):
            payload = (
                b"\r\n" + begin + b"\r\n"
                b"Linux NVTEVM 5.10.168 #1\r\n"
                b"\r\n" + end + b":0\r\n"
                b"root@NVTEVM:~$ WRN:ctl_ipp_buf_alloc() new buffer failed\r\n"
            )
            send(conn, payload)

        result, _ = self.run_session(response, fragment_size=5)
        self.assertIn("Linux NVTEVM", result.output)
        self.assertEqual(result.exit_code, 0)
        self.assertEqual(result.completion, "shell")

    def test_prompt_like_log_text_is_not_a_completion_marker(self):
        def response(conn, begin, end, send, _request):
            payload = (
                b"\r\n" + begin + b"\r\n"
                b"first line\r\n"
                b"WRN: a log line containing $ and # prompt-like text\r\n"
                b"root@NVTEVM:~$ still command output\r\n"
                b"last line\r\n"
                b"\r\n" + end + b":0\r\n"
            )
            send(conn, payload)

        result, _ = self.run_session(response, fragment_size=3)
        self.assertIn("still command output", result.output)
        self.assertIn("last line", result.output)

    def test_nonzero_shell_exit_code_is_reported_separately_from_transport(self):
        def response(conn, begin, end, send, _request):
            send(conn, b"\r\n" + begin + b"\r\ncommand failed\r\n" + end + b":7\r\n")

        result, _ = self.run_session(response)
        self.assertEqual(result.output, "command failed")
        self.assertEqual(result.exit_code, 7)

    def test_output_is_bounded_and_full_capture_is_streamed_to_file(self):
        output = (b"0123456789abcdef" * 131072)

        def response(conn, begin, end, send, _request):
            send(conn, b"\r\n" + begin + b"\r\n")
            send(conn, output)
            send(conn, b"\r\n" + end + b":0\r\n")

        with tempfile.TemporaryDirectory() as directory:
            capture_path = os.path.join(directory, "serial-output.log")
            result, _ = self.run_session(
                response, timeout=8, max_capture_bytes=1024,
                capture_path=capture_path
            )
            self.assertTrue(result.output_truncated)
            self.assertLess(len(result.output.encode("utf-8")), 3000)
            with open(capture_path, "rb") as capture:
                self.assertEqual(capture.read(), output)

    def test_timeout_marks_session_uncertain_and_never_retries_command(self):
        def response(conn, begin, _end, send, _request):
            send(conn, b"\r\n" + begin + b"\r\ncommand may have run\r\n")
            time.sleep(0.5)

        with FakeShellServer(response) as server:
            session = SerialTcpSession("127.0.0.1", server.port, default_timeout=0.2)
            self.assertTrue(session.connect())
            try:
                with self.assertRaises(CommandCompletionTimeoutError) as caught:
                    session.exec_command_result("touch /tmp/once", completion="shell", timeout=0.2)
                self.assertTrue(caught.exception.command_may_have_executed)
                with self.assertRaises(SerialTcpError):
                    session.exec_command_result("touch /tmp/once", completion="shell", timeout=0.2)
                self.assertEqual(len(server.requests), 1)
            finally:
                session.close()

    def test_reported_default_prompt_regex_is_sensitive_to_async_log_suffix(self):
        pattern = re.compile(DEFAULT_PROMPT_RE.encode())
        prompt = b"\r\nroot@NVTEVM:~$ "
        with_log = prompt + b"WRN:ctl_ipp_buf_alloc() new buffer failed\r\n"
        self.assertIsNotNone(pattern.search(prompt))
        self.assertIsNotNone(pattern.search(with_log))
        fake_prompt = b"\r\nWRN:ctl_ipp_buf_alloc() emitted a fake # prompt \r\n"
        self.assertIsNone(pattern.search(fake_prompt))

    def test_echoed_wrapper_and_async_suffix_do_not_cause_false_completion(self):
        def response(conn, begin, end, send, request):
            send(conn, request)
            send(conn, b"\r\n" + begin + b"\r\nactual output\r\n" + end + b":0\r\n")
            send(conn, b"root@NVTEVM:~$ WRN:async log after prompt\r\n")

        result, _ = self.run_session(response, fragment_size=7)
        self.assertEqual(result.output, "actual output")
        self.assertEqual(result.exit_code, 0)

    def test_read_available_bounds_memory_and_streams_full_log_to_file(self):
        payload = b"serial-log-line\r\n" * 50000
        reader, writer = socket.socketpair()
        reader.setblocking(False)
        session = SerialTcpSession("127.0.0.1", 1)
        session.sock = reader

        def produce():
            writer.sendall(payload)
            time.sleep(0.02)
            writer.close()

        thread = threading.Thread(target=produce, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory() as directory:
                capture_path = os.path.join(directory, "async.log")
                preview = session.read_available(
                    duration=0.5, max_bytes=256, capture_path=capture_path
                )
                self.assertLessEqual(len(preview), 256)
                self.assertTrue(session.last_read_truncated)
                self.assertEqual(session.last_read_bytes, len(payload))
                with open(capture_path, "rb") as capture:
                    self.assertEqual(capture.read(), payload)
        finally:
            session.close()
            thread.join(timeout=2)

    def test_pending_async_data_is_bounded_and_reports_truncation(self):
        session = SerialTcpSession("127.0.0.1", 1)
        session._store_pending(b"x" * (2 * 1024 * 1024))
        self.assertEqual(len(session._pending), 1024 * 1024)
        self.assertTrue(session._pending_truncated)
        preview = session.read_available(duration=0, max_bytes=128)
        self.assertLessEqual(len(preview), 128)
        self.assertTrue(session.last_read_truncated)
        self.assertEqual(session.last_read_bytes, 1024 * 1024)

    def test_prompt_timeout_also_blocks_blind_command_retry(self):
        reader, writer = socket.socketpair()
        reader.setblocking(False)
        session = SerialTcpSession("127.0.0.1", 1, default_timeout=0.1)
        session.sock = reader
        received = []

        def device():
            received.append(writer.recv(4096))
            writer.sendall(b"command output without a prompt\r\n")
            time.sleep(0.25)

        thread = threading.Thread(target=device, daemon=True)
        thread.start()
        try:
            with self.assertRaises(CommandCompletionTimeoutError):
                session.exec_command_result("reboot", completion="prompt", timeout=0.1)
            with self.assertRaises(SerialTcpError):
                session.exec_command_result("reboot", completion="prompt", timeout=0.1)
            self.assertEqual(len(received), 1)
        finally:
            session.close()
            writer.close()
            thread.join(timeout=2)

    def test_async_logs_before_and_after_shell_markers_remain_readable(self):
        def response(conn, begin, end, send, request):
            self.assertTrue(request.startswith(b"{\n"))
            send(
                conn,
                b"root@NVTEVM:~$ > > WRN:background before marker\r\n"
                + begin + b"\r\n"
                b"command result\r\n" + end + b":0\r\n"
                b"root@NVTEVM:~$ ERR:background after marker\r\n"
            )

        with FakeShellServer(response) as server:
            session = SerialTcpSession("127.0.0.1", server.port, default_timeout=2)
            self.assertTrue(session.connect())
            try:
                result = session.exec_command_result("uname -a", completion="shell", timeout=2)
                self.assertEqual(result.output, "command result")
                asynchronous = session.read_available(duration=0, max_bytes=4096)
                self.assertIn(b"WRN:background before marker", asynchronous)
                self.assertIn(b"ERR:background after marker", asynchronous)
            finally:
                session.close()

    def test_capture_write_failure_locks_session_when_command_completion_is_unknown(self):
        class BrokenCapture:
            def write(self, _data):
                raise OSError("disk full")

            def flush(self):
                pass

            def close(self):
                pass

        def response(conn, begin, _end, send, _request):
            send(conn, b"\r\n" + begin + b"\r\n" + (b"large output " * 4096))

        with FakeShellServer(response) as server:
            session = SerialTcpSession("127.0.0.1", server.port, default_timeout=2)
            self.assertTrue(session.connect())
            try:
                with mock.patch("builtins.open", return_value=BrokenCapture()):
                    with self.assertRaises(CommandCaptureError) as caught:
                        session.exec_command_result(
                            "cat /var/log/messages", completion="shell",
                            timeout=2, capture_path="/fake/capture.log"
                        )
                self.assertFalse(caught.exception.command_completed)
                self.assertTrue(caught.exception.command_may_have_executed)
                with self.assertRaises(SerialTcpError):
                    session.exec_command_result("reboot", completion="shell", timeout=2)
                self.assertEqual(len(server.requests), 1)
            finally:
                session.close()


if __name__ == "__main__":
    unittest.main()
