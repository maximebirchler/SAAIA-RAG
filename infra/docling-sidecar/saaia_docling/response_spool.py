"""Bounded, private response files; no model, OCR or document-content changes."""
from __future__ import annotations

import asyncio
from contextlib import contextmanager
from dataclasses import dataclass
import hashlib
import json
import logging
import os
from pathlib import Path
import re
import tempfile
from threading import Lock
from time import monotonic
import weakref

DEFAULT_RESPONSE_BYTES = 512 * 1024 * 1024
MINIMUM_RESPONSE_BYTES = 1024 * 1024
ABSOLUTE_MAXIMUM_RESPONSE_BYTES = 2 * 1024 * 1024 * 1024
RESPONSE_LIMIT_SETTING = "SAAIA_DOCLING_MAX_RESPONSE_BYTES"


def response_byte_limit_from_environment(environment=None):
    values = os.environ if environment is None else environment
    if RESPONSE_LIMIT_SETTING not in values:
        return DEFAULT_RESPONSE_BYTES
    value = values[RESPONSE_LIMIT_SETTING]
    if type(value) is not str or re.fullmatch(r"[1-9][0-9]{0,9}", value) is None:
        raise ValueError("Docling response byte limit must be an explicit integer.")
    limit = int(value)
    if not MINIMUM_RESPONSE_BYTES <= limit <= ABSOLUTE_MAXIMUM_RESPONSE_BYTES:
        raise ValueError("Docling response byte limit must be between 1 MiB and 2 GiB.")
    return limit


MAXIMUM_RESPONSE_BYTES = response_byte_limit_from_environment()
CHUNK_BYTES = 64 * 1024
LOGGER = logging.getLogger("saaia_docling.payload")
PHASES = frozenset({"upstream_download", "json_parse", "regional_repairs", "json_serialize", "wire_send", "cleanup"})


class ResponseSizeLimitError(ValueError):
    """A complete response cannot fit the configured transport ceiling."""


class _ClientDisconnected(Exception):
    pass


def _rss_bytes():
    try:
        for line in Path("/proc/self/status").read_text().splitlines():
            if line.startswith("VmRSS:"):
                return int(line.split()[1]) * 1024
    except (OSError, ValueError, IndexError):
        pass
    return None


class PayloadDiagnostics:
    """Anonymous process-local phase facts, not semantic or client-delivery proof."""

    def __init__(self):
        self._lock = Lock()
        self._phases = {}

    @contextmanager
    def phase(self, name):
        if name not in PHASES:
            raise ValueError("Unknown payload diagnostic phase.")
        started, rss_before, stats, outcome = monotonic(), _rss_bytes(), {"bytes": None}, "completed"
        with self._lock:
            row = self._phases.setdefault(name, {"started": 0, "active": 0, "completed": 0, "failed": 0, "canceled": 0})
            row["started"] += 1
            row["active"] += 1
        LOGGER.info("Payload phase=%s state=started rss_bytes=%s", name, rss_before)
        try:
            yield stats
        except (asyncio.CancelledError, _ClientDisconnected):
            outcome = "canceled"
            raise
        except BaseException:
            outcome = "failed"
            raise
        finally:
            duration = max(0, round((monotonic() - started) * 1000))
            rss_after = _rss_bytes()
            with self._lock:
                row["active"] -= 1
                row[outcome] += 1
                row.update(lastDurationMs=duration, lastBytes=stats["bytes"],
                           lastRssBeforeBytes=rss_before, lastRssAfterBytes=rss_after)
            LOGGER.info("Payload phase=%s state=%s duration_ms=%s bytes=%s rss_bytes=%s",
                        name, outcome, duration, stats["bytes"], rss_after)

    def snapshot(self):
        with self._lock:
            return {"scope": "response_file_phases_in_this_wrapper_process",
                    "maximumResponseBytes": MAXIMUM_RESPONSE_BYTES,
                    "maximumResidentConversionPayloads": 1,
                    "phases": {key: dict(value) for key, value in self._phases.items()},
                    "rssSamplesArePhaseBoundariesNotPeak": True,
                    "asgiSendIsNotClientReceiptProof": True,
                    "upstreamModelExecutionCancellationProven": False}


class PayloadWorkspace:
    """Owned private directory, independent of the uploaded PDF's context lifetime."""

    def __init__(self, diagnostics):
        self.diagnostics = diagnostics
        self._temporary = tempfile.TemporaryDirectory(prefix="saaia-docling-response-")
        self.root = Path(self._temporary.name)
        self.upstream = self.root / "upstream.json"
        self.serialized = self.root / "response.json"
        self.closed = False

    def close(self):
        if not self.closed:
            with self.diagnostics.phase("cleanup"):
                self._temporary.cleanup()
                self.closed = True


@dataclass(frozen=True)
class ResponseFile:
    path: Path
    bytes: int
    sha256: str


async def spool_upstream(chunks, workspace, *, maximum=MAXIMUM_RESPONSE_BYTES):
    """Consume an async byte stream; never ask httpx for response.content/json()."""
    if type(maximum) is not int or not 0 < maximum <= MAXIMUM_RESPONSE_BYTES:
        raise ValueError("Response byte ceiling invalid.")
    count, digest = 0, hashlib.sha256()
    with workspace.diagnostics.phase("upstream_download") as stats:
        stats["bytes"] = 0
        with workspace.upstream.open("xb") as target:
            async for block in chunks:
                if count + len(block) > maximum:
                    raise ResponseSizeLimitError("Docling response exceeds the transport byte ceiling.")
                target.write(block)
                digest.update(block)
                count += len(block)
                stats["bytes"] = count
    return ResponseFile(workspace.upstream, count, digest.hexdigest())


def parse_upstream(workspace):
    with workspace.diagnostics.phase("json_parse") as stats:
        stats["bytes"] = workspace.upstream.stat().st_size
        # TextIO releases its full byte decoder input before JSON parsing. The
        # decoded tree remains necessary for the existing regional repairs.
        with workspace.upstream.open("r", encoding="utf-8-sig") as source:
            payload = json.load(source)
        # Its receipt is already known. Do not retain a redundant disk copy or
        # its cgroup-charged page cache throughout repair/serialization/delivery.
        workspace.upstream.unlink()
        return payload


def serialize_payload(payload, workspace, *, maximum=MAXIMUM_RESPONSE_BYTES):
    """Same JSON settings as Starlette; never builds one whole Unicode/UTF8 body."""
    if type(maximum) is not int or not 0 < maximum <= MAXIMUM_RESPONSE_BYTES:
        raise ValueError("Response byte ceiling invalid.")
    count, digest = 0, hashlib.sha256()
    encoder = json.JSONEncoder(ensure_ascii=False, allow_nan=False, indent=None, separators=(",", ":"))
    with workspace.diagnostics.phase("json_serialize") as stats:
        with workspace.serialized.open("xb") as target:
            for text in encoder.iterencode(payload):
                # A base64 token can be large; UTF8 encoding itself is also bounded.
                for offset in range(0, len(text), CHUNK_BYTES):
                    block = text[offset:offset + CHUNK_BYTES].encode("utf-8")
                    if count + len(block) > maximum:
                        raise ResponseSizeLimitError("Repaired Docling response exceeds the transport byte ceiling.")
                    target.write(block)
                    digest.update(block)
                    count += len(block)
                    stats["bytes"] = count
    return ResponseFile(workspace.serialized, count, digest.hexdigest())


class OwnedResponseBody:
    """ASGI file sender that cleans up even on disconnect, send error or cancel.

    Weak finalization is only a fallback for a response abandoned before its ASGI
    call. Normal completion and every ASGI failure use an explicit finally.
    """

    def __init__(self, workspace, response_file):
        path = response_file.path
        if (workspace.closed or path.parent != workspace.root or path.is_symlink()
                or not path.is_file() or path.stat().st_size != response_file.bytes
                or not 0 <= response_file.bytes <= MAXIMUM_RESPONSE_BYTES):
            raise ValueError("Owned response file invalid.")
        self.workspace, self.file = workspace, response_file
        self._cleanup = weakref.finalize(self, workspace.close)

    def close(self):
        self._cleanup()

    async def send(self, scope, receive, send, *, status_code, headers):
        tasks = []
        try:
            with self.workspace.diagnostics.phase("wire_send") as stats:
                stats["bytes"] = 0

                async def body():
                    with self.file.path.open("rb") as source:
                        await send({"type": "http.response.start", "status": status_code, "headers": headers})
                        while True:
                            block = source.read(CHUNK_BYTES)
                            if not block:
                                break
                            await send({"type": "http.response.body", "body": block, "more_body": True})
                            stats["bytes"] += len(block)
                        await send({"type": "http.response.body", "body": b"", "more_body": False})

                async def disconnected():
                    while (await receive())["type"] != "http.disconnect":
                        pass

                tasks = [asyncio.create_task(body()), asyncio.create_task(disconnected())]
                done, _ = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
                if tasks[0] in done:
                    await tasks[0]
                else:
                    await tasks[1]
                    raise _ClientDisconnected()
        except _ClientDisconnected:
            # A disconnected client is a normal end of ASGI delivery, while its
            # phase is recorded as canceled rather than a completed transfer.
            pass
        finally:
            try:
                for task in tasks:
                    if not task.done():
                        task.cancel()
                if tasks:
                    await asyncio.gather(*tasks, return_exceptions=True)
            finally:
                self.close()
