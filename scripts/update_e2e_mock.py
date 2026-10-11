# -*- coding: utf-8 -*-
"""升级闭环 E2E 模拟源：Release API + 安装包下载（本地 HTTP）"""
import http.server, json, socketserver, threading, os, sys, functools

PORT = 18080
SETUP = r"E:\WorkFile\CodeWareHouse\UartSoftware\dist\installer\SerialBridge-Setup-1.3.0-x64.exe"

class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith("/api/releases/latest"):
            body = json.dumps({
                "tag_name": "v1.3.0",
                "draft": False,
                "prerelease": False,
                "assets": [{"name": "SerialBridge-Setup-1.3.0-x64.exe",
                             "browser_download_url": f"http://127.0.0.1:{PORT}/SerialBridge-Setup-1.3.0-x64.exe"}],
            }).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        elif self.path.startswith("/SerialBridge-Setup-"):
            size = os.path.getsize(SETUP)
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(size))
            self.end_headers()
            with open(SETUP, "rb") as f:
                while True:
                    chunk = f.read(65536)
                    if not chunk: break
                    self.wfile.write(chunk)
        else:
            self.send_response(404); self.end_headers()
    def log_message(self, *a): pass

srv = socketserver.TCPServer(("127.0.0.1", PORT), Handler)
print(f"mock update server on {PORT}", flush=True)
srv.serve_forever()
