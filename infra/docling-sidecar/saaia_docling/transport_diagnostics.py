"""Process-local transport counters; no document names, text or identifiers."""
from __future__ import annotations

from contextlib import contextmanager
from threading import Lock
from time import monotonic
from typing import Callable, Iterator


class ConversionTransportDiagnostics:
    def __init__(self) -> None:
        self._lock = Lock()
        self._active = 0
        self._started = 0
        self._outcomes = {"success": 0, "upstream_rejected": 0, "failed": 0}
        self._last_duration_ms: int | None = None

    @contextmanager
    def observe(self) -> Iterator[Callable[[bool], None]]:
        # This scope belongs inside the synchronous forwarding thread. Canceling
        # its asyncio waiter must not pretend that the underlying HTTP work ended.
        started = monotonic()
        outcome = "failed"
        with self._lock:
            self._started += 1
            self._active += 1

        def response_received(success: bool) -> None:
            nonlocal outcome
            outcome = "success" if success else "upstream_rejected"

        try:
            yield response_received
        finally:
            with self._lock:
                self._active -= 1
                self._outcomes[outcome] += 1
                self._last_duration_ms = max(0, round((monotonic() - started) * 1000))

    def snapshot(self) -> dict[str, object]:
        with self._lock:
            return {"scope": "upstream_http_forwarding_threads_in_this_process",
                    "active": self._active, "started": self._started,
                    "finished": sum(self._outcomes.values()), **self._outcomes,
                    "lastFinishedDurationMs": self._last_duration_ms,
                    "upstreamModelExecutionCancellationProven": False}
