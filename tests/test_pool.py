"""Tests for claude_pool.py: python test_pool.py [path to claude_pool.py]"""
import base64
import http.client
import http.server
import importlib.util
import json
import os
from pathlib import Path
import socket
import sys
import tempfile
import threading
import time

source = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "claude_pool.py"
spec = importlib.util.spec_from_file_location("claude_pool", source)
pool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pool)

NOW = 1_800_000_000
failed = 0
received, opened = [], []  # requests the fake API got, and the connections made to it


def test(name):
    def run(body):
        global failed
        try:
            body()
            print("PASS", name)
        except Exception as error:
            failed += 1
            print("FAIL", name, ":", repr(error))
    return run


DAY = 86400


@test("会话的账号没到上限就不换，到上限换成周额度最先作废的那个，没开始计周的留着")
def _():
    seen = {"A": {"5h": [0.5, NOW + 99]}, "B": {"7d": [0.4, NOW + 3 * DAY]}, "C": {"7d": [0.1, NOW + 6 * DAY]}}
    assert pool.choose(["A", "B", "C", "D"], seen, "A", None, NOW) == "A"
    seen["A"]["5h"] = [0.96, NOW + 99]
    assert pool.choose(["A", "B", "C", "D"], seen, "A", None, NOW) == "B"
    assert pool.choose(["C", "D"], seen, None, None, NOW) == "C"
    assert pool.choose(["D", "E"], {"E": {"7d": [0.07, NOW + 158 * 3600]}}, None, None, NOW) == "E"


@test("新会话和闲置回来的会话跟着正在用的账号走，用到余量不足才换")
def _():
    seen = {"A": {"7d": [0.44, NOW + 111 * 3600]}, "B": {"7d": [0.34, NOW + 83 * 3600]}}
    assert pool.choose(["A", "B"], seen, None, None, NOW, current="A") == "A"
    assert pool.choose(["A", "B"], seen, None, None, NOW) == "B"
    seen["A"]["5h"] = [0.9, NOW + 99]
    assert pool.choose(["A", "B"], seen, None, None, NOW, current="A") == "B"
    seen["A"]["5h"] = [0.2, NOW + 99]
    seen["B"]["7d"] = [0.6, NOW + 3 * 3600]
    assert pool.choose(["A", "B"], seen, None, None, NOW, current="A") == "B"


@test("新会话避开 5 小时额度快满的账号，全都到线时留在原账号")
def _():
    seen = {"A": {"5h": [0.9, NOW + 99], "7d": [0.5, NOW + DAY]}, "B": {"7d": [0.2, NOW + 5 * DAY]}}
    assert pool.choose(["A", "B"], seen, None, None, NOW) == "B"
    seen["B"]["5h"] = [0.97, NOW + 99]
    assert pool.choose(["A", "B"], seen, None, None, NOW) == "A"
    seen["A"]["5h"] = [0.98, NOW + 99]
    assert pool.choose(["A", "B"], seen, "B", None, NOW) == "B"


@test("别的账号周额度快作废且剩得多时，正在聊的会话也换过去用掉它")
def _():
    seen = {"A": {"7d": [0.2, NOW + 5 * DAY]}, "B": {"7d": [0.6, NOW + 3 * 3600]}}
    assert pool.choose(["A", "B"], seen, "A", None, NOW) == "B"
    assert "3 小时后重置、还剩 40%" in pool.reason(seen, "A", "B", None, NOW)
    seen["B"]["7d"] = [0.6, NOW + 2 * DAY]
    assert pool.choose(["A", "B"], seen, "A", None, NOW) == "A"
    seen["B"]["7d"] = [0.9, NOW + 3 * 3600]
    assert pool.choose(["A", "B"], seen, "A", None, NOW) == "A"


@test("在用付费超额的账号先让给别的，别的也用完了才接着用")
def _():
    info = {}
    pool.observe(info, {"anthropic-ratelimit-unified-overage-in-use": "true", "anthropic-ratelimit-unified-reset": str(NOW + 3600)}, 200, NOW)
    seen = {"A": info, "B": {"7d": [0.5, NOW + 5 * DAY]}}
    assert pool.choose(["A", "B"], seen, "A", None, NOW) == "B"
    seen["B"]["blocked_until"] = NOW + 99
    assert pool.choose(["A", "B"], seen, "A", None, NOW) == "A"


@test("过了重置时间的窗口按全新算")
def _():
    seen = {"A": {"5h": [1.0, NOW - 1], "7d": [0.3, NOW + 99]}}
    assert pool.used(seen["A"], NOW) == 0.3
    assert pool.choose(["A", "B"], seen, "A", None, NOW) == "A"


@test("额度用完的 429 锁到重置，其他 429 锁一分钟，失效凭证锁一天，全锁住时不换凭证")
def _():
    info = {}
    pool.observe(info, {"anthropic-ratelimit-unified-status": "rejected", "anthropic-ratelimit-unified-reset": str(NOW + 3600),
                        "anthropic-ratelimit-unified-5h-utilization": "1.0", "anthropic-ratelimit-unified-5h-reset": str(NOW + 3600)}, 429, NOW)
    assert info["blocked_until"] == NOW + 3600 and info["5h"] == [1.0, NOW + 3600]
    burst = {}
    pool.observe(burst, {}, 429, NOW)
    assert burst["blocked_until"] == NOW + 60
    revoked = {}
    pool.observe(revoked, {}, 401, NOW)
    assert revoked["blocked_until"] == NOW + 86400
    seen = {"A": info, "B": burst}
    assert pool.choose(["A", "B"], seen, "A", None, NOW) is None
    assert pool.choose(["A", "B"], seen, "A", None, NOW + 61) == "B"


@test("固定的账号优先，被锁住时照常挑别的")
def _():
    seen = {"B": {"5h": [0.99, NOW + 99]}}
    assert pool.choose(["A", "B"], seen, "A", "B", NOW) == "B"
    seen["B"]["blocked_until"] = NOW + 99
    assert pool.choose(["A", "B"], seen, "A", "B", NOW) == "A"


def connect():
    """A client connection to a proxy handler, as Claude Code opens one."""
    near, far = socket.socketpair()
    threading.Thread(target=pool.ApiHandler, args=(far, ("test", 0), None), daemon=True).start()
    client = http.client.HTTPConnection("proxy")
    client.sock = near
    return client


@test("请求被 429 时换下一个账号重发，回复原样流回去，其他接口不换凭证")
def _():
    work = Path(tempfile.mkdtemp())
    pool.CONFIG, pool.STATE, pool.LOG = work / "pool.json", work / "state.json", work / "pool.log"
    seal = lambda token: base64.b64encode(pool.dpapi(token.encode(), True)).decode()
    pool.write_json(pool.CONFIG, {"enabled": True, "accounts": [{"name": "A", "token": seal("token-a")}, {"name": "B", "token": seal("token-b")}]})
    pool.write_json(pool.STATE, {"current": "A", "accounts": {}, "switch": None, "sessions": {"s1": ["A", time.time()]}})
    class Upstream(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):
            pass

        def setup(self):
            super().setup()
            opened.append(self.client_address[1])

        def do_POST(self):
            self.rfile.read(int(self.headers["Content-Length"]))
            received.append((self.path, self.headers["Authorization"], self.client_address[1]))
            if self.path == "/plain":  # a body sent with a length, as error replies are
                self.send_response(400)
                self.send_header("Content-Length", "2")
                self.end_headers()
                self.wfile.write(b"{}")
                return
            if self.headers["Authorization"] == "Bearer token-a" and self.path.startswith("/v1/messages"):
                self.send_response(429)
                self.send_header("anthropic-ratelimit-unified-status", "rejected")
                self.send_header("anthropic-ratelimit-unified-reset", str(int(time.time()) + 3600))
                self.send_header("Content-Length", "2")
                self.end_headers()
                self.wfile.write(b"{}")
                return
            self.send_response(200)
            self.send_header("anthropic-ratelimit-unified-5h-utilization", "0.25")
            self.send_header("anthropic-ratelimit-unified-5h-reset", str(int(time.time()) + 3600))
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
            for part in (b"data: one\n\n", b"data: two\n\n"):
                self.wfile.write(b"%x\r\n%s\r\n" % (len(part), part))
            self.wfile.write(b"0\r\n\r\n")

    global upstream
    upstream = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
    threading.Thread(target=upstream.serve_forever, daemon=True).start()
    pool.http.client.HTTPSConnection = lambda host, timeout: http.client.HTTPConnection("127.0.0.1", upstream.server_port, timeout=timeout)
    pool.ApiHandler.pool = pool.Pool()
    pool.ApiHandler.upstream = pool.Upstream()

    client = connect()
    client.request("POST", "/v1/messages?beta=true", body=b'{"x":1}', headers={"Authorization": "Bearer desktop", "X-Claude-Code-Session-Id": "s1"})
    reply = client.getresponse()
    assert reply.status == 200 and reply.read() == b"data: one\n\ndata: two\n\n", "reply not streamed back"
    client.request("POST", "/api/event_logging/v2/batch", body=b"{}", headers={"Authorization": "Bearer desktop"})
    assert client.getresponse().read() is not None
    assert [r[:2] for r in received] == [("/v1/messages?beta=true", "Bearer token-a"), ("/v1/messages?beta=true", "Bearer token-b"),
                                         ("/api/event_logging/v2/batch", "Bearer desktop")], received
    state = json.loads(pool.STATE.read_text(encoding="utf-8"))
    assert state["current"] == "B" and state["switch"]["from"] == "A" and state["accounts"]["A"]["blocked_until"] > time.time()
    assert state["sessions"]["s1"][0] == "B"


@test("新开的客户端连接直接用已连好的上游连接，不再握手")
def _():
    time.sleep(0.2)  # the spare opened ahead during the last test
    before = len(opened)
    client = connect()
    client.request("POST", "/api/event_logging/v2/batch", body=b"{}", headers={"Authorization": "Bearer desktop"})
    client.getresponse().read()
    assert received[-1][2] in opened[:before] and len(opened) <= before + 1, (before, opened)


@test("带长度的回复转完后，那条上游连接还能接着用")
def _():
    client = connect()
    for _ in range(2):
        client.request("POST", "/plain", body=b"{}", headers={"Authorization": "Bearer desktop"})
        reply = client.getresponse()
        assert reply.status == 400 and reply.read() == b"{}"
    assert received[-1][2] == received[-2][2], received[-2:]


@test("网络出错（不只是连接被关）时代理用新连接重试，请求照常成功")
def _():
    class Broken:
        def request(self, *args, **kwargs):
            raise FileNotFoundError(2, "No such file or directory")  # as logged during a network outage

        def close(self):
            pass
    pool.ApiHandler.upstream = pool.Upstream()
    for _ in range(3):  # every kept connection is dead, as after the network came back
        pool.ApiHandler.upstream.give(Broken())
    client = connect()
    client.request("POST", "/api/event_logging/v2/batch", body=b"{}", headers={"Authorization": "Bearer desktop"})
    assert client.getresponse().status == 200
    upstream.shutdown()


@test("端口被占时代理不启动，并在日志里写明原因")
def _():
    taken = socket.create_server(("127.0.0.1", 0))
    pool.PORT = taken.getsockname()[1]
    pool.serve()
    taken.close()
    assert f"port {pool.PORT} is taken" in pool.LOG.read_text(encoding="utf-8")


@test("会话守着自己的账号；闲置超过缓存时长再回来时重新挑，不算一次切换")
def _():
    pool.write_json(pool.STATE, {"current": None, "accounts": {"A": {"7d": [0.5, time.time() + 3 * DAY]}}, "switch": None})
    keeper = pool.Pool()
    assert keeper.pick("s1")[0] == "A"
    keeper.state["accounts"]["B"] = {"7d": [0.5, time.time() + DAY]}
    assert keeper.pick("s1")[0] == "A" and keeper.pick("s2")[0] == "A"
    keeper.state["accounts"]["A"]["5h"] = [0.9, time.time() + 99]  # too little left to bring a conversation to it
    assert keeper.pick("s1")[0] == "A"
    keeper.state["sessions"]["s1"][1] -= pool.AFFINITY
    assert keeper.pick("s1")[0] == "B" and keeper.state["switch"] is None


print(f"RESULT failed={failed}")
sys.exit(failed)
