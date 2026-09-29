"""PASS/FAIL rung envelopes for verify-style tools.

A FAIL rung without a file:line citation cannot be built — a rung that cannot
cite code is omitted by the caller, never emitted uncited (spec Tool
contracts). Scope labels come from registry.SCOPES.
"""
import registry


def rung(verdict, detail="", file=None, expected=None, actual=None,
         scope="rpg-server-debug"):
    if scope not in registry.SCOPES:
        raise ValueError(f"unknown scope '{scope}'; closed set: {registry.SCOPES}")
    if verdict == "FAIL" and not file:
        raise ValueError("FAIL rung requires file:line evidence")
    return {
        "verdict": verdict,
        "detail": detail,
        "file": file,
        "expected": expected,
        "actual": actual,
        "scope": scope,
    }
