"""Plain JSON-over-HTTP and server-sent events with the standard library only.

Like scripts/dev_stack.py and scripts/check-subscriptions.py, the harness runs from a bare checkout
with nothing installed.
"""

import json
import queue
import threading
import time
import urllib.error
import urllib.request


class HttpError(RuntimeError):
    def __init__(self, method, url, status, body):
        super().__init__(f"{method} {url} -> {status}: {body[:500]}")
        self.status = status
        self.body = body


def request(method, url, body=None, headers=None, timeout=15.0):
    """The response body parsed as JSON (None when empty); raises HttpError on a non-2xx status."""
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    for key, value in (headers or {}).items():
        req.add_header(key, value)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode()
    except urllib.error.HTTPError as e:
        raise HttpError(method, url, e.code, e.read().decode(errors="replace")) from None
    if not raw.strip():
        return None
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return raw


def sse(url, headers=None, seconds=10.0):
    """Yields (event name, parsed data) from a server-sent event stream until `seconds` have passed.

    The stream is read on a thread of its own: a socket read timeout would let the deadline be
    checked, but http.client cannot resume a chunked body after one.
    """
    req = urllib.request.Request(url)
    req.add_header("Accept", "text/event-stream")
    for key, value in (headers or {}).items():
        req.add_header(key, value)
    deadline = time.monotonic() + seconds
    resp = urllib.request.urlopen(req, timeout=30)
    lines = queue.Queue()

    def pump():
        try:
            for raw in resp:
                lines.put(raw.decode().rstrip("\r\n"))
        except Exception:
            pass
        lines.put(None)

    threading.Thread(target=pump, daemon=True).start()
    try:
        event, data = None, []
        while True:
            left = deadline - time.monotonic()
            if left <= 0:
                return
            try:
                line = lines.get(timeout=left)
            except queue.Empty:
                return
            if line is None:
                return
            if line == "":
                if data:
                    payload = "\n".join(data)
                    try:
                        yield event, json.loads(payload)
                    except json.JSONDecodeError:
                        yield event, payload
                event, data = None, []
            elif line.startswith(":"):
                continue
            elif line.startswith("event:"):
                event = line[len("event:"):].strip()
            elif line.startswith("data:"):
                data.append(line[len("data:"):].lstrip())
    finally:
        resp.close()
