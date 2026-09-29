"""HTTP + event-cursor client for FusionRpg LIVE proves (stdlib only)."""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Any


class LiveClient:
    def __init__(self, base_url: str = "http://127.0.0.1:5088", timeout: float = 15.0):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def _url(self, path: str, query: dict[str, Any] | None = None) -> str:
        if not path.startswith("/"):
            path = "/" + path
        url = self.base_url + path
        if query:
            q = {k: v for k, v in query.items() if v is not None}
            url += "?" + urllib.parse.urlencode(q)
        return url

    def request(
        self,
        method: str,
        path: str,
        body: dict[str, Any] | None = None,
        query: dict[str, Any] | None = None,
    ) -> Any:
        data = None
        headers = {"Accept": "application/json"}
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(self._url(path, query), data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                raw = resp.read().decode("utf-8")
                if not raw:
                    return None
                return json.loads(raw)
        except urllib.error.HTTPError as e:
            err_body = e.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"{method} {path} -> HTTP {e.code}: {err_body}") from e
        except urllib.error.URLError as e:
            raise RuntimeError(f"{method} {path} failed: {e}") from e

    def get(self, path: str, **query: Any) -> Any:
        return self.request("GET", path, query=query or None)

    def post(self, path: str, body: dict[str, Any] | None = None) -> Any:
        return self.request("POST", path, body=body if body is not None else {})

    def health(self) -> dict[str, Any]:
        return self.get("/health")

    def post_debug(self, path: str, body: dict[str, Any] | None = None) -> Any:
        if not path.startswith("/"):
            path = "/" + path
        return self.post("/api/debug" + path, body)

    def post_cheat_toggle(self, cheat_id: str, enabled: bool = True) -> Any:
        return self.post("/api/cheats/toggle", {"id": cheat_id, "enabled": enabled})

    def events(self, after_id: int = 0, limit: int = 100, kinds: str | None = None) -> list[dict[str, Any]]:
        q: dict[str, Any] = {"afterId": after_id, "limit": limit}
        if kinds:
            q["kinds"] = kinds
        page = self.get("/api/events", **q)
        return list(page.get("items") or [])

    def max_event_id(self) -> int:
        def has_after(eid: int) -> bool:
            return len(self.events(after_id=eid, limit=1)) > 0

        if not has_after(0):
            return 0
        lo, hi = 0, 1
        while has_after(hi):
            lo = hi
            if hi > (1 << 62):
                break
            hi *= 2
        while lo + 1 < hi:
            mid = (lo + hi) // 2
            if has_after(mid):
                lo = mid
            else:
                hi = mid
        return lo

    @staticmethod
    def payload(ev: dict[str, Any] | None) -> Any:
        if not ev:
            return None
        p = ev.get("payload")
        if p is None:
            return None
        if isinstance(p, str):
            try:
                return json.loads(p)
            except json.JSONDecodeError:
                return None
        return p

    def wait_kind(
        self,
        after_id: int,
        kind: str,
        timeout_sec: float = 15.0,
        poll_ms: int = 200,
    ) -> dict[str, Any] | None:
        deadline = time.time() + timeout_sec
        cursor = after_id
        while time.time() < deadline:
            items = self.events(after_id=cursor, limit=100)
            hits = [e for e in items if e.get("kind") == kind]
            if hits:
                return hits[-1]
            if items:
                cursor = int(items[-1]["id"])
            time.sleep(poll_ms / 1000.0)
        return None

    def post_and_wait(
        self,
        path: str,
        kind: str,
        body: dict[str, Any] | None = None,
        timeout_sec: float = 20.0,
    ) -> tuple[Any, dict[str, Any] | None]:
        tip = self.max_event_id()
        queued = self.post_debug(path, body)
        ev = self.wait_kind(tip, kind, timeout_sec=timeout_sec)
        return queued, ev
