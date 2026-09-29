"""PASS/FAIL reporting."""

from __future__ import annotations


class Report:
    def __init__(self, name: str):
        self.name = name
        self.rows: list[tuple[bool, str]] = []

    def check(self, ok: bool, msg: str) -> None:
        self.rows.append((ok, msg))
        tag = "OK" if ok else "FAIL"
        print(f"  [{tag}] {msg}")

    def require(self, ok: bool, msg: str) -> None:
        self.check(ok, msg)
        if not ok:
            raise AssertionError(msg)

    def ok(self) -> bool:
        return all(r[0] for r in self.rows)

    def summary(self) -> int:
        failed = sum(1 for ok, _ in self.rows if not ok)
        print(f"== {self.name}: {'PASS' if failed == 0 else 'FAIL'} ({len(self.rows) - failed}/{len(self.rows)}) ==")
        return 0 if failed == 0 else 1
