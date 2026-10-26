from __future__ import annotations

import asyncio
from dataclasses import asdict
import json
import os
from pathlib import Path
import signal
import sys
import tempfile
from typing import Any

from .table_repair import OcrLine
from .response_text_repair import validate_text_region
from .text_repair import TextOcrObservation
from .text_repair_options import TextRepairOptions


_POSIX_PROCESS_GROUPS = os.name == "posix"


def build_region_request(pdf_path, document, item, options, timeout_seconds):
    validate_text_region(document, item, options)
    page_number = item["prov"][0]["page_no"]
    page = document["pages"].get(str(page_number))
    if not isinstance(page, dict):
        page = next(value for value in document["pages"].values()
                    if isinstance(value, dict) and value.get("page_no") == page_number)
    if not isinstance(item.get("text"), str) or len(item["text"]) > 8_000:
        raise ValueError("Regional OCR request text exceeds its budget.")
    source_region = item["prov"][0]
    return {
        "pdf_path": str(pdf_path),
        "document": {"pages": {str(page_number): {"page_no": page_number,
            "size": {key: page["size"][key] for key in ("width", "height")}}}},
        "item": {"self_ref": item["self_ref"], "text": item["text"], "label": item.get("label"),
                 "prov": [{"page_no": page_number,
                           "bbox": {key: source_region["bbox"][key] for key in ("l", "t", "r", "b", "coord_origin")
                                    if key in source_region["bbox"]}}]},
        "options": asdict(options),
        "timeout_seconds": max(0.1, timeout_seconds - 1),
    }


class BoundedTextRegionEngine:
    """A Linux process group owns rendering, RapidOCR and its Tesseract child.

    Cancellation reaps that group before releasing its private temporary directory.
    No background thread is represented as cancelled while it still runs.
    """

    def __init__(self) -> None:
        self._slot = asyncio.Semaphore(1)
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
            "upstream_docling_cancellation_proven": False,
        }

    async def recognize(
        self, pdf_path: Path, document: dict[str, Any], item: dict[str, Any],
        options: TextRepairOptions, timeout_seconds: float,
    ) -> tuple[TextOcrObservation, TextOcrObservation]:
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
        options: TextRepairOptions, timeout_seconds: float,
    ) -> tuple[TextOcrObservation, TextOcrObservation]:
        if not _POSIX_PROCESS_GROUPS:
            raise RuntimeError("Regional OCR process isolation requires Linux.")
        # Reserve part of the approved region budget for termination/reaping.
        if timeout_seconds <= 0.2:
            raise TimeoutError()
        loop = asyncio.get_running_loop()
        deadline = loop.time() + min(timeout_seconds, options.region_timeout_seconds)
        with tempfile.TemporaryDirectory(prefix="saaia-text-region-") as directory:
            root = Path(directory)
            request_path, output_path = root / "request.json", root / "result.json"
            request = json.dumps(build_region_request(pdf_path, document, item, options, timeout_seconds),
                                 ensure_ascii=False, allow_nan=False)
            if len(request.encode("utf-8")) > 65_536:
                raise ValueError("Regional OCR request exceeds its byte budget.")
            request_path.write_text(request, encoding="utf-8")
            process = None
            try:
                process = await asyncio.create_subprocess_exec(
                    sys.executable, "-B", "-m", "saaia_docling.text_region_worker",
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
                return _decode_observation(result["rapid"]), _decode_observation(result["secondary"])
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


def _kill_owned_group(pid: int) -> None:
    os.killpg(pid, signal.SIGKILL)


def _owned_group_exists(pid: int) -> bool:
    try:
        os.killpg(pid, 0)
    except ProcessLookupError:
        return False
    return True


def _decode_observation(value: dict[str, Any]) -> TextOcrObservation:
    if not isinstance(value, dict) or not isinstance(value.get("lines"), list):
        raise ValueError("OCR observation schema is invalid.")
    lines = tuple(OcrLine(line["text"], line["confidence"], tuple(tuple(point) for point in line["polygon"])) for line in value["lines"])
    return TextOcrObservation(
        value["engine"], value["engine_version"], value["model_id"], lines,
        value.get("duration_ms", 0), value.get("model_sha256"), value.get("model_artifacts"), value.get("runtime_settings"),
    )
