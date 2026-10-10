# -*- coding: utf-8 -*-
"""假串口设备（模拟 SerialBridge 的占用拒绝 + 串口终端行为）——测试基础设施"""
import socket, threading, time

class FakeDevice:
    def __init__(self, port, require_login=False, max_clients=1):
        self.srv = socket.socket()
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind(("127.0.0.1", port))
        self.srv.listen(4)
        self.port = port
        self.require_login = require_login
        self.max_clients = max_clients
        self.nclients = 0
        self.alive = True
        threading.Thread(target=self._loop, daemon=True).start()

    def _loop(self):
        while self.alive:
            try:
                c, _ = self.srv.accept()
            except OSError:
                return
            if self.nclients >= self.max_clients:
                c.close()   # SerialBridge 单客户端拒绝行为：立即关闭
                continue
            self.nclients += 1
            threading.Thread(target=self._serve, args=(c,), daemon=True).start()

    def _serve(self, c):
        c.setblocking(False)
        buf = b""
        if self.require_login:
            c.sendall(b"board login: ")
        else:
            c.sendall(b"\r\n~ # ")
        stage = "user" if self.require_login else "shell"
        while self.alive:
            try:
                data = c.recv(256)
                if not data:
                    break
                buf += data
            except BlockingIOError:
                time.sleep(0.01)
                continue
            except OSError:
                break
            while b"\r" in buf:
                idx = buf.find(b"\r")
                line, buf = buf[:idx], buf[idx+1:]
                if stage == "user":
                    c.sendall(b"\r\nPassword: ")
                    stage = "pass"
                elif stage == "pass":
                    c.sendall(b"\r\nWelcome\r\n~ # ")
                    stage = "shell"
                else:
                    out = b"Linux testbox 5.10.0-arm" if b"uname" in line else b"OK"
                    c.sendall(b"\r\n" + line + b"\r\n" + out + b"\r\n~ # ")
        self.nclients -= 1
        try: c.close()
        except OSError: pass
