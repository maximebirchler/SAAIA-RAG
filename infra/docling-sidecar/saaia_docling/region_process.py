"""Shared process ownership for bounded regional OCR; no model loads on import."""
from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path
import signal
import sys
import tempfile
from typing import Any

_POSIX_PROCESS_GROUPS = os.name == "posix"


class RegionProcessGate(asyncio.Semaphore):
    """A cleanup failure blocks both OCR modes until the service is restarted."""

    def __init__(self):
        super().__init__(1)
        self.poisoned = False

    def poison(self):
        self.poisoned = True

    async def acquire(self):
        if self.poisoned:
            raise RuntimeError("Regional OCR process ownership is unconfirmed.")
        acquired = await super().acquire()
        if self.poisoned:
            super().release()
            raise RuntimeError("Regional OCR process ownership is unconfirmed.")
        return acquired


class BoundedRegionEngine:
    """A Linux process group owns rendering, OCR and any engine children.

    Cancellation reaps that group before releasing its private temporary directory.
    No background thread is represented as cancelled while it still runs.
    """

    def __init__(self, request_builder, result_decoder, *, worker_module, shared_slot=None) -> None:
        self._request_builder = request_builder
        self._result_decoder = result_decoder
        self._worker_module = worker_module
        if shared_slot is not None and not isinstance(shared_slot, RegionProcessGate):
            raise ValueError("Regional OCR requires a shared ownership gate.")
        self._slot = shared_slot if shared_slot is not None else RegionProcessGate()
        self._process_started = 0
        self._process_reaped = 0
        self._active_processes = 0
        self._cleanup_unconfirmed = 0
        self._groups_confirmed_absent = 0
        self._canceled = 0
        self._timed_out = 0

    def snapshot(self) -> dict[str, object]:
        return {
            "processes_started": self._process_started,
            "processes_reaped": self._process_reaped,
            "active_or_unconfirmed_processes": self._active_processes,
            "cleanup_unconfirmed": self._cleanup_unconfirmed,
            "groups_confirmed_absent": self._groups_confirmed_absent,
            "canceled": self._canceled,
            "timed_out": self._timed_out,
            "shared_ocr_gate_poisoned": self._slot.poisoned,
            "upstream_docling_cancellation_proven": False,
        }

    async def recognize(
        self, pdf_path: Path, document: dict[str, Any], item: dict[str, Any],
        options, timeout_seconds: float,
    ):
        loop = asyncio.get_running_loop()
        deadline = loop.time() + min(timeout_seconds, options.region_timeout_seconds)
        acquired = False
        try:
            await asyncio.wait_for(self._slot.acquire(), timeout=max(0.001, deadline - loop.time()))
            acquired = True
            remaining = deadline - loop.time()
            if remaining <= 0.2:
                raise TimeoutError()
            return await self._recognize_owned(pdf_path, document, item, options, remaining)
        except asyncio.CancelledError:
            self._canceled += 1
            raise
        except TimeoutError:
            self._timed_out += 1
            raise
        finally:
            if acquired:
                self._slot.release()

    async def _recognize_owned(
        self, pdf_path: Path, document: dict[str, Any], item: dict[str, Any],
        options, timeout_seconds: float,
    ):
        if not _POSIX_PROCESS_GROUPS:
            raise RuntimeError("Regional OCR process isolation requires Linux.")
        # Reserve part of the approved region budget for termination/reaping.
        if timeout_seconds <= 0.2:
            raise TimeoutError()
        loop = asyncio.get_running_loop()
        deadline = loop.time() + min(timeout_seconds, options.region_timeout_seconds)
        with tempfile.TemporaryDirectory(prefix="saaia-ocr-region-") as directory:
            root = Path(directory)
            request_path, output_path = root / "request.json", root / "result.json"
            request = json.dumps(self._request_builder(pdf_path, document, item, options, timeout_seconds),
                                 ensure_ascii=False, allow_nan=False)
            if len(request.encode("utf-8")) > 65_536:
                raise ValueError("Regional OCR request exceeds its byte budget.")
            request_path.write_text(request, encoding="utf-8")
            process = None
            try:
                process = await asyncio.create_subprocess_exec(
                    sys.executable, "-B", "-m", self._worker_module,
                    str(request_path), str(output_path),
                    stdin=asyncio.subprocess.DEVNULL,
                    stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL,
                    start_new_session=True,
                    env={**os.environ, "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1",
                         "OMP_THREAD_LIMIT": "1", "PYTHONNOUSERSITE": "1"},
                )
                self._process_started += 1
                self._active_processes += 1
                remaining = deadline - loop.time() - min(1.0, timeout_seconds / 4)
                if remaining <= 0:
                    raise TimeoutError()
                await asyncio.wait_for(process.wait(), timeout=remaining)
                if process.returncode != 0 or not output_path.is_file() or output_path.stat().st_size > 2_000_000:
                    raise RuntimeError("Regional OCR did not return a bounded result.")
                result = json.loads(output_path.read_text(encoding="utf-8"))
                if not isinstance(result, dict) or result.get("status") != "success":
                    raise RuntimeError("Regional OCR engines unavailable or invalid.")
                if loop.time() >= deadline:
                    raise TimeoutError()
                decoded = self._result_decoder(result)
                if loop.time() >= deadline:
                    raise TimeoutError()
                return decoded
            finally:
                if process is not None:
                    # Even an exited leader could have left a descendant. Only the
                    # freshly created group is targeted; no process-name/global kill.
                    group_absence_confirmed = False
                    try:
                        try:
                            _kill_owned_group(process.pid)
                        except ProcessLookupError:
                            group_absence_confirmed = True
                        if process.returncode is None:
                            remaining = deadline - loop.time()
                            if remaining <= 0:
                                raise RuntimeError("Regional OCR cleanup deadline exceeded.")
                            try:
                                await asyncio.wait_for(asyncio.shield(process.wait()), timeout=remaining)
                            except asyncio.TimeoutError as exc:
                                raise RuntimeError("Regional OCR cleanup was not confirmed.") from exc
                        if not group_absence_confirmed:
                            while _owned_group_exists(process.pid):
                                remaining = deadline - loop.time()
                                if remaining <= 0:
                                    raise RuntimeError("Regional OCR group cleanup was not confirmed.")
                                await asyncio.sleep(min(0.01, remaining))
                            group_absence_confirmed = True
                    finally:
                        if process.returncode is not None:
                            self._process_reaped += 1
                        if process.returncode is not None and group_absence_confirmed:
                            self._active_processes -= 1
                            self._groups_confirmed_absent += 1
                        else:
                            self._cleanup_unconfirmed += 1
                            self._slot.poison()


def _kill_owned_group(pid: int) -> None:
    os.killpg(pid, signal.SIGKILL)


def _owned_group_exists(pid: int) -> bool:
    try:
        os.killpg(pid, 0)
    except ProcessLookupError:
        return False
    return True
