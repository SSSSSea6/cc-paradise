"""Local HTTPS proxy that serves Claude Code's model requests from whichever of several
Claude subscriptions still has quota, so a chat goes on when one account runs out.

Only the rotation window uses it: that Claude is started with HTTPS_PROXY pointing here,
and its Code sessions inherit it. Requests to api.anthropic.com are opened with a local CA;
/v1/messages calls get the chosen account's token, everything else passes through
untouched. Quota comes from the anthropic-ratelimit-unified-* headers of the responses
themselves; the proxy never sends requests of its own.

    claude_pool.py serve | trust | pause | resume | remove NAME | pin NAME|auto
                   add NAME (token on stdin) | import NAME DESKTOP_PROFILE_FOLDER
"""
import base64
import ctypes
from ctypes import wintypes
import datetime
import http.client
import http.server
import json
import os
from pathlib import Path
import socket
import ssl
import sys
import threading
import time
import traceback

HOME = Path(os.environ["LOCALAPPDATA"]) / "ClaudeSwitcher"  # tokens, certificates and state; never next to the code
CONFIG = HOME / "pool.json"
STATE = HOME / "pool-state.json"
LOG = HOME / "pool.log"
CERTS = HOME / "proxy"
SETTINGS = Path.home() / ".claude" / "settings.json"
PORT = 47630
API = "api.anthropic.com"
SWITCH_AT = 0.95                      # leave an account this close to either limit
HEADROOM = 0.10                       # a new conversation wants at least this much room
AFFINITY = 3600                       # a conversation idle this long has lost its prompt cache anyway
RESCUE_WITHIN, RESCUE_LEFT = 12 * 3600, 0.15  # weekly quota worth a cache miss before it resets unused
WEEK = 7 * 86400
KEEP = 240                            # seconds an idle API connection is kept for reuse (the API keeps them 5+ minutes)
HOP = {"connection", "keep-alive", "transfer-encoding", "te", "trailer", "upgrade", "proxy-connection", "content-length"}


# ---------------------------------------------------------------- choosing --

def used(seen, now):
    """The larger share used of the 5-hour and weekly windows; a window past its reset counts as fresh."""
    seen = seen or {}
    return max([u for u, reset in (seen.get("5h", (0, 0)), seen.get("7d", (0, 0))) if reset > now] or [0])


def blocked(seen, now):
    return (seen or {}).get("blocked_until", 0) > now


def room(seen, now):
    """How far an account is from SWITCH_AT; none while it is drawing on paid extra usage."""
    return 0 if (seen or {}).get("overage_until", 0) > now else SWITCH_AT - used(seen, now)


def weekly(seen, now):
    """(share left, seconds to reset) of the weekly window; one not started yet has the whole week ahead."""
    u, reset = (seen or {}).get("7d", (0, 0))
    return (1 - u, reset - now) if reset > now else (1, WEEK)


def urgency(seen, now):
    """Weekly quota left per hour until it resets: what is lost if this account sits idle.
    A week not started loses nothing by waiting."""
    u, reset = (seen or {}).get("7d", (0, 0))
    return (1 - u) / max(reset - now, 3600) if reset > now else 0


def expiring(seen, now):
    left, seconds = weekly(seen, now)
    return seconds < RESCUE_WITHIN and left - (1 - SWITCH_AT) >= RESCUE_LEFT


def choose(names, seen, assigned, pinned, now, exclude=(), current=None):
    """The account for a conversation's next request.

    Accounts are used up one at a time. A conversation keeps its account while that has room, so
    its prompt cache stays warm; a new one, or one back after its cache expired, joins the account
    in use while that has room to spare. Either moves early only when another account's weekly
    quota is about to reset unused. Otherwise it goes to the account that would waste the most by
    waiting (soonest weekly reset, most left), preferring ones with room to spare; accounts whose
    week has not started are kept for later. With every account at its limit it stays put, or
    takes the one with the most left."""
    usable = [n for n in names if n not in exclude and not blocked(seen.get(n), now)]
    if pinned in usable:
        return pinned
    roomy = [n for n in usable if room(seen.get(n), now) > 0]
    if not roomy:
        return assigned if assigned in usable else min(usable, key=lambda n: used(seen.get(n), now), default=None)
    best = max(roomy, key=lambda n: (room(seen.get(n), now) >= HEADROOM, urgency(seen.get(n), now)))

    def keeps(name, need):
        return (name in roomy and room(seen.get(name), now) >= need
                and not (expiring(seen.get(best), now) and urgency(seen.get(best), now) > urgency(seen.get(name), now)))
    if keeps(assigned, 0):
        return assigned
    if keeps(current, HEADROOM):
        return current
    return best


def reason(seen, assigned, name, pinned, now):
    """Why a conversation moved, completing「<assigned>」…"""
    if name == pinned:
        return "手动指定了新账号"
    if blocked(seen.get(assigned), now):
        return "额度已用完"
    if room(seen.get(assigned), now) <= 0:
        return "额度快用完"
    left, seconds = weekly(seen.get(name), now)
    return f"额度还有，但「{name}」的周额度 {max(1, round(seconds / 3600))} 小时后重置、还剩 {round(left * 100)}% 没用"


def observe(info, headers, status, now):
    """Folds one response's quota headers into what is known of an account."""
    def header(name):
        return headers.get("anthropic-ratelimit-unified-" + name)
    for window in ("5h", "7d"):
        if header(window + "-utilization") is not None:
            info[window] = [float(header(window + "-utilization")), int(header(window + "-reset") or 0)]
    if header("overage-in-use") == "true":
        # Included quota is gone and the request was billed as extra usage; prefer the others until it resets.
        info["overage_until"] = int(header("reset") or now + 3600)
    if status == 429:
        # Out of quota: rejected until the window resets. Any other 429 is a short burst limit.
        rejected = header("status") == "rejected"
        info["blocked_until"] = int(header("reset") or now + 300) if rejected else now + 60
    elif status == 401:
        # The token was revoked, e.g. by signing out of that account; set aside for a day.
        info["blocked_until"] = now + 86400
    info["seen"] = now


# ------------------------------------------------------------------ storage --

class Blob(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_char))]


def dpapi(data, protect):
    buffer = ctypes.create_string_buffer(data, len(data))
    incoming = Blob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_char)))
    outgoing = Blob()
    call = ctypes.windll.crypt32.CryptProtectData if protect else ctypes.windll.crypt32.CryptUnprotectData
    if not call(ctypes.byref(incoming), None, None, None, None, 1, ctypes.byref(outgoing)):
        raise ctypes.WinError()
    try:
        return ctypes.string_at(outgoing.data, outgoing.size)
    finally:
        ctypes.windll.kernel32.LocalFree(outgoing.data)


def read_json(path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except FileNotFoundError:
        return default


def write_json(path, data):
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def log(message):
    if LOG.exists() and LOG.stat().st_size > 1 << 20:
        LOG.unlink()
    with LOG.open("a", encoding="utf-8") as f:
        f.write(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {message}\n")


class Pool:
    """Accounts from pool.json, re-read when it changes, and what has been seen of each."""

    def __init__(self):
        self.lock = threading.Lock()
        self.state = read_json(STATE, {"current": None, "accounts": {}, "switch": None})
        self.mtime = None
        self.config = {}
        self.tokens = {}

    def refresh(self):
        mtime = CONFIG.stat().st_mtime if CONFIG.exists() else None
        if mtime != self.mtime:
            self.mtime = mtime
            self.config = read_json(CONFIG, {})
            self.tokens = {a["name"]: dpapi(base64.b64decode(a["token"]), False).decode()
                           for a in self.config.get("accounts", [])}

    def pick(self, session, exclude=()):
        """The account to send a conversation's next request with, or None to pass the request through."""
        with self.lock:
            self.refresh()
            if self.config.get("paused") or not self.tokens:
                return None, None
            now = time.time()
            seen = self.state["accounts"]
            sessions = {k: v for k, v in self.state.get("sessions", {}).items() if now - v[1] < AFFINITY}
            assigned = sessions.get(session, [None])[0]
            pinned = self.config.get("pinned")
            name = choose(list(self.tokens), seen, assigned, pinned, now, exclude, self.state.get("current"))
            if name and assigned in self.tokens and name != assigned:
                why = reason(seen, assigned, name, pinned, now)
                self.state["switch"] = {"from": assigned, "to": name, "reason": why, "at": now}
                log(f"switch {assigned} -> {name} ({why})")
            if name:
                sessions[session] = [name, now]
                self.state["current"] = name
            self.state["sessions"] = sessions
            write_json(STATE, self.state)
            return name, self.tokens.get(name)

    def record(self, name, headers, status):
        with self.lock:
            observe(self.state["accounts"].setdefault(name, {}), headers, status, time.time())
            write_json(STATE, self.state)


# -------------------------------------------------------------------- proxy --

def ensure_certs():
    """A CA kept for good, and a certificate for api.anthropic.com renewed before it expires."""
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

    CERTS.mkdir(parents=True, exist_ok=True)
    now = datetime.datetime.now(datetime.timezone.utc)
    pem = serialization.Encoding.PEM
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Claude Switcher Local CA")])
    if not (CERTS / "ca.key").exists():
        key = ec.generate_private_key(ec.SECP256R1())
        ca = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
              .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(days=1))
              .not_valid_after(now + datetime.timedelta(days=3650))
              .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
              .add_extension(x509.KeyUsage(False, False, False, False, False, True, True, False, False), critical=True)
              .sign(key, hashes.SHA256()))
        (CERTS / "ca.key").write_bytes(key.private_bytes(pem, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
        (CERTS / "ca.pem").write_bytes(ca.public_bytes(pem))
    leaf_path = CERTS / "api.pem"
    if leaf_path.exists():
        expires = x509.load_pem_x509_certificate(leaf_path.read_bytes()).not_valid_after_utc
        if expires - now > datetime.timedelta(days=30):
            return
    ca_key = serialization.load_pem_private_key((CERTS / "ca.key").read_bytes(), None)
    key = ec.generate_private_key(ec.SECP256R1())
    leaf = (x509.CertificateBuilder().subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, API)]))
            .issuer_name(name).public_key(key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(days=1)).not_valid_after(now + datetime.timedelta(days=397))
            .add_extension(x509.SubjectAlternativeName([x509.DNSName(API)]), critical=False)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
            .sign(ca_key, hashes.SHA256()))
    (CERTS / "api.key").write_bytes(key.private_bytes(pem, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    leaf_path.write_bytes(leaf.public_bytes(pem) + (CERTS / "ca.pem").read_bytes())


class Upstream:
    """Kept-alive connections to the API, shared by all of Claude Code's connections. Whenever
    none is spare another is opened ahead, so a new connection or a parallel request does not
    wait for a TLS handshake."""

    def __init__(self):
        self.lock = threading.Lock()
        self.idle = []  # (connection, idle since)

    def take(self):
        """A connection, and whether it was used before (the server may have closed it since)."""
        now = time.time()
        with self.lock:
            stale = [c for c, since in self.idle if now - since >= KEEP]
            self.idle = [(c, since) for c, since in self.idle if now - since < KEEP]
            conn = self.idle.pop()[0] if self.idle else None
            spare = bool(self.idle)
        for c in stale:
            c.close()
        if not spare:
            threading.Thread(target=self.warm, daemon=True).start()
        return (conn, True) if conn else (self.fresh(), False)

    @staticmethod
    def fresh():
        return http.client.HTTPSConnection(API, timeout=600)

    def give(self, conn):
        with self.lock:
            self.idle.append((conn, time.time()))

    def warm(self):
        conn = self.fresh()
        try:
            conn.connect()
        except OSError:
            return
        self.give(conn)


class ApiHandler(http.server.BaseHTTPRequestHandler):
    """One opened connection from Claude Code to api.anthropic.com."""
    protocol_version = "HTTP/1.1"
    pool = None
    upstream = None

    def log_message(self, *args):
        pass

    def read_body(self):
        if self.headers.get("Transfer-Encoding", "").lower() != "chunked":
            return self.rfile.read(int(self.headers.get("Content-Length") or 0))
        body = b""
        while size := int(self.rfile.readline().split(b";")[0].strip(), 16):
            body += self.rfile.read(size)
            self.rfile.readline()
        self.rfile.readline()
        return body

    def send_upstream(self, headers, body):
        """(connection, response). A kept-alive connection the server closed meanwhile is replaced
        at once by a new one (never another kept one, which may be closed too); any other network
        error, such as a brief DNS or proxy outage, gets that one more try a second later instead
        of failing the request."""
        for attempt in (1, 2):
            conn, reused = self.upstream.take() if attempt == 1 else (self.upstream.fresh(), False)
            try:
                conn.request(self.command, self.path, body=body, headers=headers)
                return conn, conn.getresponse()
            except TimeoutError:
                conn.close()
                raise
            except (OSError, http.client.HTTPException):
                conn.close()
                if attempt == 2:
                    raise
                if not reused:
                    time.sleep(1)

    def finish_with(self, conn, response):
        """Returns a connection to the shared ones once its response is done. A body sent with a
        length is not marked done by reading it to its end with read1, and a connection still
        holding it fails its next request, so what is left of it (nothing) is read first."""
        if not response.isclosed():
            response.read()
        if response.will_close:
            conn.close()
        else:
            self.upstream.give(conn)

    def relay(self, response):
        self.send_response(response.status)
        no_body = self.command == "HEAD" or response.status in (204, 304)
        for key, value in response.getheaders():
            if key.lower() not in HOP or (no_body and key.lower() == "content-length"):
                self.send_header(key, value)
        if no_body:
            self.end_headers()
            return
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()
        while chunk := response.read1(65536):
            self.wfile.write(b"%x\r\n%s\r\n" % (len(chunk), chunk))
            self.wfile.flush()
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()

    def forward(self):
        body = self.read_body()
        headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP}
        session = self.headers.get("X-Claude-Code-Session-Id", "")
        tried = []
        while True:
            name, token = self.pool.pick(session, tried) if self.path.startswith("/v1/messages") else (None, None)
            if token:
                headers = {k: v for k, v in headers.items() if k.lower() != "authorization"}
                headers["Authorization"] = "Bearer " + token
            try:
                conn, response = self.send_upstream(headers, body)
            except Exception as error:
                log(f"upstream failed: {error!r}")
                self.send_error(502, "Claude account pool could not reach api.anthropic.com")
                return
            if name:
                self.pool.record(name, {k.lower(): v for k, v in response.getheaders()}, response.status)
                if response.status in (401, 429):
                    tried.append(name)
                    if self.pool.pick(session, tried)[0]:
                        response.read()
                        self.finish_with(conn, response)
                        continue
            try:
                self.relay(response)
            except Exception:
                conn.close()  # Claude Code went away mid-reply; the rest of it is unread
                raise
            self.finish_with(conn, response)
            return

    do_GET = do_POST = do_PUT = do_DELETE = do_PATCH = do_HEAD = do_OPTIONS = forward


def pipe(source, target):
    try:
        while data := source.recv(65536):
            target.sendall(data)
    except OSError:
        pass
    finally:
        for s in (source, target):
            try:
                s.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass


def handle(client, address, tls):
    try:
        head = b""
        while b"\r\n\r\n" not in head:
            chunk = client.recv(4096)
            if not chunk:
                return
            head += chunk
        method, target, _ = head.split(b"\r\n", 1)[0].decode("latin-1").split(" ", 2)
        if method != "CONNECT":
            client.sendall(b"HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n")
            return
        host, port = target.rsplit(":", 1)
        if host == API:
            client.sendall(b"HTTP/1.1 200 Connection Established\r\n\r\n")
            ApiHandler(tls.wrap_socket(client, server_side=True), address, None)
            return
        upstream = socket.create_connection((host, int(port)), timeout=30)
        upstream.settimeout(None)
        client.sendall(b"HTTP/1.1 200 Connection Established\r\n\r\n")
        threading.Thread(target=pipe, args=(upstream, client), daemon=True).start()
        pipe(client, upstream)
    except (OSError, ValueError):
        client.close()


def serve():
    # Started windowless by the tray, so whatever stops it from starting goes to the log.
    try:
        server = socket.create_server(("127.0.0.1", PORT))
    except OSError as error:
        log(f"not started, port {PORT} is taken (another proxy already running?): {error!r}")
        return
    try:
        ensure_certs()
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(CERTS / "api.pem", CERTS / "api.key")
        tls.set_alpn_protocols(["http/1.1"])
        ApiHandler.pool = Pool()
        ApiHandler.upstream = Upstream()
    except Exception:
        log("not started:\n" + traceback.format_exc())
        raise
    log("proxy started")
    while True:
        client, address = server.accept()
        threading.Thread(target=handle, args=(client, address, tls), daemon=True).start()


# ----------------------------------------------------------------- commands --

def read_desktop_token(folder):
    """The longest-lived model token a signed-in desktop profile holds (about a year).
    Electron seals its AES key with DPAPI behind a "DPAPI" prefix in Local State; the token
    cache is "v10" + 12-byte nonce + AES-GCM ciphertext."""
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    key = dpapi(base64.b64decode(read_json(folder / "Local State", {})["os_crypt"]["encrypted_key"])[5:], False)
    blob = base64.b64decode(read_json(folder / "config.json", {})["oauth:tokenCacheV2"])
    entries = json.loads(AESGCM(key).decrypt(blob[3:15], blob[15:], None))
    return max((e for scope, e in entries.items() if "user:inference" in scope), key=lambda e: e["expiresAt"])["token"]


def save_account(config, name, token):
    sealed = base64.b64encode(dpapi(token.encode(), True)).decode()
    config["accounts"] = [a for a in config["accounts"] if a["name"] != name] + [{"name": name, "token": sealed}]
    write_json(CONFIG, config)


def main():
    command, args = sys.argv[1], sys.argv[2:]
    HOME.mkdir(exist_ok=True)
    config = read_json(CONFIG, {})
    accounts = config.setdefault("accounts", [])
    if command == "serve":
        serve()
    elif command == "trust":
        # The desktop app drops NODE_EXTRA_CA_CERTS from the environment its windows pass on,
        # so the CA is named in Claude Code's own settings instead. Trusting it routes nothing:
        # only the rotation window is pointed at the proxy.
        settings = read_json(SETTINGS, {})
        env = settings.setdefault("env", {})
        if env.get("NODE_EXTRA_CA_CERTS") != str(CERTS / "ca.pem"):
            env["NODE_EXTRA_CA_CERTS"] = str(CERTS / "ca.pem")
            write_json(SETTINGS, settings)
    elif command in ("pause", "resume"):
        config["paused"] = command == "pause"
        write_json(CONFIG, config)
    elif command == "add":
        token = sys.stdin.read().strip()
        if not token.startswith("sk-ant-") or any(c.isspace() for c in token):
            sys.exit("这不像 claude setup-token 给出的令牌（应以 sk-ant- 开头）。")
        save_account(config, args[0], token)
    elif command == "import":
        try:
            token = read_desktop_token(Path(args[1]))
        except (OSError, KeyError, ValueError):
            sys.exit("这个窗口里没有可用的登录，先打开它登录一次。")
        save_account(config, args[0], token)
    elif command == "remove":
        config["accounts"] = [a for a in accounts if a["name"] != args[0]]
        if config.get("pinned") == args[0]:
            config["pinned"] = None
        write_json(CONFIG, config)
    elif command == "pin":
        config["pinned"] = None if args[0] == "auto" else args[0]
        write_json(CONFIG, config)


if __name__ == "__main__":
    main()
