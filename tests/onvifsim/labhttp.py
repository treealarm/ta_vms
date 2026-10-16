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
import uuid


class HttpError(RuntimeError):
    def __init__(self, method, url, status, body):
        super().__init__(f"{method} {url} -> {status}: {body[:500]}")
        self.status = status
        self.body = body


def request(method, url, body=None, headers=None, timeout=15.0):
    """The response body parsed as JSON (None when empty); raises HttpError on a non-2xx status."""
    data = None if body is None else json.dumps(body).encode()
    return _json(_send(method, url, data, "application/json" if data is not None else None, headers, timeout))


def request_multipart(url, fields, file_field, filename, content, mime, headers=None, timeout=60.0):
    """POSTs a form with text fields and one file, as a browser's file upload does; the response
    parsed as JSON. Built by hand: the standard library has no multipart encoder."""
    boundary = f"lab-{uuid.uuid4().hex}"
    parts = []
    for name, value in fields.items():
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
    parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="{file_field}"; filename="{filename}"\r\n'
                  f"Content-Type: {mime}\r\n\r\n").encode() + content + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    return _json(_send("POST", url, b"".join(parts), f"multipart/form-data; boundary={boundary}", headers, timeout))


def fetch_bytes(url, headers=None, timeout=30.0):
    """The raw response body, for what is not JSON (a picture)."""
    return _send("GET", url, None, None, headers, timeout, accept="*/*")


def _send(method, url, data, content_type, headers, timeout, accept="application/json"):
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Accept", accept)
    if content_type:
        req.add_header("Content-Type", content_type)
    for key, value in (headers or {}).items():
        req.add_header(key, value)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.read()
    except urllib.error.HTTPError as e:
        raise HttpError(method, url, e.code, e.read().decode(errors="replace")) from None


def _json(raw):
    raw = raw.decode()
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
