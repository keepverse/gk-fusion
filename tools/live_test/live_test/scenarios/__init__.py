"""Scenario protocol + registry."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Callable

from live_test.client import LiveClient
from live_test.report import Report


@dataclass
class RunContext:
    client: LiveClient
    force_setup: bool = False
    amount: int = -150
    enter_level: bool = False
    target_ptr: str | None = None


ScenarioFn = Callable[[RunContext, Report], None]

REGISTRY: dict[str, ScenarioFn] = {}


def scenario(name: str) -> Callable[[ScenarioFn], ScenarioFn]:
    def wrap(fn: ScenarioFn) -> ScenarioFn:
        REGISTRY[name] = fn
        return fn

    return wrap


def list_scenarios() -> list[str]:
    return sorted(REGISTRY.keys())
