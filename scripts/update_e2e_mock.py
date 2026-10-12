# -*- coding: utf-8 -*-
"""升级闭环 E2E 模拟源：Release 列表 API + 限流兜底重定向 + 安装包下载（本地 HTTP）

用法：
  set SERIALBRIDGE_UPDATE_API=http://127.0.0.1:18080/api/releases
  set SERIALBRIDGE_UPDATE_FALLBACK=http://127.0.0.1:18080/releases/latest
  SerialBridge.exe
"""
import http.server, json, socketserver, os

PORT = 18080
SETUP = r"E:\WorkFile\CodeWareHouse\UartSoftware\dist\installer\SerialBridge-Setup-1.3.0-x64.exe"
TAG = "v1.3.0"
VERSION = "1.3.0"
ASSET = f"SerialBridge-Setup-{VERSION}-x64.exe"

def release_json():
    return {
        "tag_name": TAG,
        "draft": False,
        "prerelease": False,
        "assets": [{"name": ASSET,
                     "browser_download_url": f"http://127.0.0.1:{PORT}/SerialBridge-Setup-{VERSION}-x64.exe"}],
    }

class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        path = self.path.split("?")[0]
        if path == "/api/releases/latest":          # 单对象（兼容旧客户端）
            self._json(json.dumps(release_json()))
        elif path == "/api/releases":               # 列表（新主路径；故意乱序验证按版本号取最大）
            self._json(json.dumps([release_json(),
                                   {"tag_name": "v1.2.9", "draft": False, "prerelease": False}]))
        elif path == "/releases/latest":            # 网页兜底：302 → /tag/vX.Y.Z
            self.send_response(302)
            self.send_header("Location", f"/owner/repo/releases/tag/{TAG}")
            self.end_headers()
        elif path == "/api/ratelimited":            # 模拟 API 限流（触发兜底路径用）
            self.send_response(403)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"message":"API rate limit exceeded"}')
        elif path.startswith("/SerialBridge-Setup-") or path.startswith("/releases/download/"):
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

    def _json(self, body):
        body = body.encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *a): pass

srv = socketserver.TCPServer(("127.0.0.1", PORT), Handler)
print(f"mock update server on {PORT}", flush=True)
srv.serve_forever()
