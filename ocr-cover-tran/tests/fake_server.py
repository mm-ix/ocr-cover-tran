#!/usr/bin/env python3
"""本地 OpenAI 兼容假翻译服务，用于 UI/稳定性测试（不联网、不产生费用）。

用法:
  python fake_server.py 8765
  FAIL_FIRST=1 python fake_server.py 8766   # 第一次请求返回 500，用于验证 R 重试
"""
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(next((a for a in sys.argv[1:] if a.isdigit()), "8765"))
FAIL_FIRST = "--fail-first" in sys.argv or os.environ.get("FAIL_FIRST", "0") == "1"
request_count = 0


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _send_json(self, code, obj):
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path.startswith("/stats"):
            self._send_json(200, {"requests": request_count, "fail_first": FAIL_FIRST})
        else:
            self._send_json(404, {"error": "not found"})

    def do_POST(self):
        global request_count
        length = int(self.headers.get("Content-Length", "0") or 0)
        raw = self.rfile.read(length)
        request_count += 1

        if FAIL_FIRST and request_count == 1:
            self._send_json(500, {"error": "simulated upstream failure"})
            return

        try:
            payload = json.loads(raw.decode("utf-8"))
            messages = payload.get("messages", [])
            user = messages[-1]["content"] if messages else "[]"
            try:
                items = json.loads(user)
                if not isinstance(items, list):
                    items = [str(items)]
            except Exception:
                items = [user]

            out = []
            for item in items:
                text = str(item)
                has_cjk = any("一" <= c <= "鿿" for c in text)
                out.append(text if has_cjk else "译文：" + text)

            content = json.dumps(out, ensure_ascii=False)
            self._send_json(200, {
                "id": "fake-" + str(request_count),
                "object": "chat.completion",
                "model": payload.get("model", "fake"),
                "choices": [{
                    "index": 0,
                    "message": {"role": "assistant", "content": content},
                    "finish_reason": "stop",
                }],
                "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
            })
        except Exception as ex:
            self._send_json(400, {"error": str(ex)})


if __name__ == "__main__":
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"READY http://127.0.0.1:{PORT}/v1/chat/completions fail_first={FAIL_FIRST}", flush=True)
    server.serve_forever()
