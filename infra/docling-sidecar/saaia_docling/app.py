from __future__ import annotations

import asyncio
import gc
import logging
import os
import tempfile
from pathlib import Path

import httpx
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse, Response
from starlette.datastructures import UploadFile

from .execution_metadata import conversion_execution_metadata
from .heading_hierarchy import HeadingHierarchyConfiguration
from .table_region_process import BoundedTableRegionEngine
from .table_repair_options import TableRepairOptions
from .response_repair import repair_response_tables
from .response_text_repair import repair_response_texts
from .text_region_process import BoundedTextRegionEngine
from .text_repair_options import TextRepairOptions
from .region_process import RegionProcessGate
from .table_observation_options import TableObservationWorkOptions, observation_settings, requested_table_observations
from .transport_diagnostics import ConversionTransportDiagnostics
from .response_spool import (
    CHUNK_BYTES, MAXIMUM_RESPONSE_BYTES, OwnedResponseBody, PayloadDiagnostics,
    PayloadWorkspace, ResponseSizeLimitError, parse_upstream, serialize_payload, spool_upstream,
)


logging.basicConfig(
    level=os.getenv("SAAIA_DOCLING_LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
LOGGER = logging.getLogger("saaia_docling")

UPSTREAM_URL = os.getenv(
    "SAAIA_DOCLING_UPSTREAM_URL",
    "http://127.0.0.1:5002",
).rstrip("/")
MAX_FILE_SIZE = min(
    max(int(os.getenv("DOCLING_SERVE_MAX_FILE_SIZE", "536870912")), 1_048_576),
    2_147_483_648,
)
UPSTREAM_TIMEOUT_SECONDS = min(
    max(int(os.getenv("SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS", "900")), 30),
    86_400,
)
REPAIR_ENABLED = os.getenv(
    "SAAIA_DOCLING_TABLE_REPAIR_ENABLED",
    "true",
).casefold() in {"1", "true", "yes", "on"}
TABLE_REPAIR = TableRepairOptions.from_environment()
RENDER_SCALE = TABLE_REPAIR.render_scale
PADDING_POINTS = TABLE_REPAIR.padding_points
MINIMUM_SCORE = TABLE_REPAIR.minimum_score
HEADING_HIERARCHY = HeadingHierarchyConfiguration.from_environment()
TEXT_REPAIR = TextRepairOptions.from_environment()
CONTROL_FIELD_NAMES = {
    "saaia_heading_hierarchy_enabled",
    "saaia_heading_hierarchy_use_bookmarks",
    "saaia_heading_hierarchy_use_numbering",
    "saaia_heading_hierarchy_use_style",
    "saaia_heading_hierarchy_max_level",
    "saaia_heading_hierarchy_bookmark_threshold",
}

app = FastAPI(
    title="SAAIA Docling sidecar",
    docs_url=None,
    redoc_url=None,
    openapi_url=None,
)
regional_ocr_slot = RegionProcessGate()
table_region_engine = BoundedTableRegionEngine(regional_ocr_slot)
text_region_engine = BoundedTextRegionEngine(regional_ocr_slot)
conversion_transport = ConversionTransportDiagnostics()
payload_diagnostics = PayloadDiagnostics()
conversion_slot = asyncio.Semaphore(1)


class OwnedFileResponse(Response):
    """The body owns its files beyond the uploaded PDF context's lifetime."""

    def __init__(self, body: OwnedResponseBody, *, status_code=200, media_type="application/json"):
        super().__init__(content=b"", status_code=status_code, media_type=media_type,
                         headers={"content-length": str(body.file.bytes)})
        self.owned_body = body

    async def __call__(self, scope, receive, send):
        await self.owned_body.send(scope, receive, send, status_code=self.status_code, headers=self.raw_headers)


@app.get("/health")
async def health() -> JSONResponse:
    try:
        async with httpx.AsyncClient(timeout=3) as client:
            response = await client.get(f"{UPSTREAM_URL}/health")
        upstream_ready = response.is_success
    except httpx.HTTPError:
        upstream_ready = False
    regional_ready = not regional_ocr_slot.poisoned
    status = 200 if upstream_ready and regional_ready else 503
    return JSONResponse(
        {
            "status": "upstream_unavailable" if not upstream_ready else
                      "regional_ocr_cleanup_unconfirmed" if not regional_ready else "ok",
            "upstream": upstream_ready,
            "regionalTableRepair": REPAIR_ENABLED,
            "regionalTableRepairSettings": TABLE_REPAIR.attestation(),
            "regionalTableRepairRuntime": table_region_engine.snapshot(),
            "regionalTextRepair": TEXT_REPAIR.attestation(),
            "regionalTextRepairRuntime": text_region_engine.snapshot(),
            "conversionTransport": conversion_transport.snapshot(),
            "conversionPayload": payload_diagnostics.snapshot(),
            "headingHierarchy": {
                "enabled": HEADING_HIERARCHY.enabled,
                "useBookmarks": HEADING_HIERARCHY.use_bookmarks,
                "useNumbering": HEADING_HIERARCHY.use_numbering,
                "useStyle": HEADING_HIERARCHY.use_style,
                "maxLevel": HEADING_HIERARCHY.max_level,
                "bookmarkMatchThreshold": (
                    HEADING_HIERARCHY.bookmark_match_threshold
                ),
            },
        },
        status_code=status,
    )


@app.post("/v1/convert/file")
async def convert_file(request: Request) -> Response:
    # Release only after _convert_file_owned has dropped its parsed payload. A
    # returned file response retains bounded file metadata, never the JSON tree.
    async with conversion_slot:
        return await _convert_file_owned(request)


async def _convert_file_owned(request: Request) -> Response:
    async with request.form(
        max_files=1,
        max_fields=64,
        max_part_size=MAX_FILE_SIZE,
    ) as form:
        uploads = [
            value
            for _, value in form.multi_items()
            if isinstance(value, UploadFile)
        ]
        if len(uploads) != 1:
            raise HTTPException(
                status_code=400,
                detail="Exactly one PDF file is required.",
            )
        upload = uploads[0]
        filename = Path(upload.filename or "document.pdf").name
        received_fields = [
            (key, str(value))
            for key, value in form.multi_items()
            if not isinstance(value, UploadFile)
        ]
        _validate_heading_hierarchy_contract(received_fields)
        try:
            observe_tables = requested_table_observations(received_fields)
        except ValueError as error:
            raise HTTPException(status_code=400, detail="Table observation request invalid.") from error
        fields = [
            (key, value)
            for key, value in received_fields
            if key not in CONTROL_FIELD_NAMES and key != "saaia_table_observations_enabled"
        ]

        with tempfile.TemporaryDirectory(prefix="saaia-docling-") as directory:
            pdf_path = Path(directory) / filename
            await _copy_bounded(upload, pdf_path)
            workspace = PayloadWorkspace(payload_diagnostics)
            response_owns_workspace = False
            payload = None
            payload_parsed = False
            try:
                upstream_file, status_code, media_type = await _forward_conversion(
                    pdf_path, filename, upload.content_type or "application/pdf", fields, workspace,
                )
                if 200 <= status_code < 300:
                    invalid_json = False
                    try:
                        payload = parse_upstream(workspace)
                        payload_parsed = True
                    except (ValueError, UnicodeError, RecursionError):
                        invalid_json = True
                    # Translate after leaving except so native parser errors,
                    # their frames and any document fragments do not escape.
                    if invalid_json:
                        raise HTTPException(status_code=502, detail="Docling returned invalid JSON.")
                    if not isinstance(payload, dict):
                        raise HTTPException(status_code=502, detail="Docling returned an invalid response envelope.")
                    with payload_diagnostics.phase("regional_repairs"):
                        table_options = TableObservationWorkOptions.from_table_options(
                            TABLE_REPAIR, repair_enabled=REPAIR_ENABLED) if observe_tables else TABLE_REPAIR
                        if (REPAIR_ENABLED or observe_tables) and payload.get("status") == "success":
                            summaries = await repair_response_tables(pdf_path, payload, table_region_engine, options=table_options)
                            if summaries:
                                LOGGER.info("Regional table repair candidates=%s applied=%s", len(summaries),
                                            sum(item.get("status") == "applied" for item in summaries))
                        if TEXT_REPAIR.enabled and payload.get("status") == "success":
                            text_summaries = await repair_response_texts(pdf_path, payload, text_region_engine, options=TEXT_REPAIR)
                            LOGGER.info("Regional text repair candidates=%s attempted=%s applied=%s",
                                        payload.get("saaia_text_repair_summary", {}).get("candidate_count", 0),
                                        sum(item.get("attempted", False) for item in text_summaries),
                                        sum(item.get("status") == "applied" for item in text_summaries))
                    payload["saaia_execution"] = conversion_execution_metadata(
                        fields, repair_enabled=REPAIR_ENABLED, render_scale=RENDER_SCALE,
                        padding_points=PADDING_POINTS, minimum_score=MINIMUM_SCORE,
                        text_repair_settings=TEXT_REPAIR.attestation(),
                        table_repair_settings=TABLE_REPAIR.attestation(),
                        table_observation_settings=observation_settings(table_options) if observe_tables else None,
                    )
                    try:
                        response_file = serialize_payload(payload, workspace)
                    except ResponseSizeLimitError:
                        LOGGER.warning("Payload refusal stage=json_serialize reason=serialized_bytes maximum_response_bytes=%s",
                                       MAXIMUM_RESPONSE_BYTES)
                        raise
                    del payload  # The next admitted conversion cannot overlap this large tree.
                    # JSONResponse historically returned 200/application-json on upstream success.
                    status_code, media_type = 200, "application/json"
                else:
                    response_file = upstream_file
                response = OwnedFileResponse(OwnedResponseBody(workspace, response_file),
                                             status_code=status_code, media_type=media_type)
                response_owns_workspace = True
                return response
            except ResponseSizeLimitError:
                failure_detail = f"Docling response exceeds the configured {MAXIMUM_RESPONSE_BYTES} byte transport limit."
            except (ValueError, UnicodeError, RecursionError):
                failure_detail = "Docling response cannot be serialized safely."
            finally:
                payload = None
                if not response_owns_workspace:
                    workspace.close()
            # An exception traceback can retain the entire JSON tree. Translate
            # after leaving except and dropping payload, before releasing the slot.
            # An aborted stdlib JSON iterator can also retain circular-check
            # markers through encoder closures after its traceback is gone.
            # Only rejected responses trigger collection; the success path does not.
            if payload_parsed:
                with payload_diagnostics.phase("error_payload_release"):
                    gc.collect()
            raise HTTPException(status_code=502, detail=failure_detail)


def _validate_heading_hierarchy_contract(
    fields: list[tuple[str, str]],
) -> None:
    expected = {
        "saaia_heading_hierarchy_enabled": str(
            HEADING_HIERARCHY.enabled
        ).lower(),
        "saaia_heading_hierarchy_use_bookmarks": str(
            HEADING_HIERARCHY.use_bookmarks
        ).lower(),
        "saaia_heading_hierarchy_use_numbering": str(
            HEADING_HIERARCHY.use_numbering
        ).lower(),
        "saaia_heading_hierarchy_use_style": str(
            HEADING_HIERARCHY.use_style
        ).lower(),
        "saaia_heading_hierarchy_max_level": str(
            HEADING_HIERARCHY.max_level
        ),
        "saaia_heading_hierarchy_bookmark_threshold": str(
            HEADING_HIERARCHY.bookmark_match_threshold
        ),
    }
    received = {
        key: value.strip().lower()
        for key, value in fields
        if key in CONTROL_FIELD_NAMES
    }
    if not received:
        return
    mismatches = [
        key
        for key, value in received.items()
        if value != expected[key]
    ]
    if mismatches:
        raise HTTPException(
            status_code=409,
            detail=(
                "Backend and Docling heading-hierarchy configuration "
                "do not match."
            ),
        )


async def _copy_bounded(upload: UploadFile, destination: Path) -> None:
    size = 0
    with destination.open("wb") as output:
        while True:
            chunk = await upload.read(128 * 1024)
            if not chunk:
                break
            size += len(chunk)
            if size > MAX_FILE_SIZE:
                raise HTTPException(
                    status_code=413,
                    detail=f"PDF exceeds the {MAX_FILE_SIZE} byte limit.",
                )
            output.write(chunk)
    if size == 0:
        raise HTTPException(status_code=400, detail="PDF file is empty.")


async def _forward_conversion(
    pdf_path: Path,
    filename: str,
    content_type: str,
    fields: list[tuple[str, str]],
    workspace: PayloadWorkspace,
) -> tuple:
    # Async stream cancellation closes the HTTP connection and file before the
    # caller cleans its workspace. It does not prove cancellation of Docling OCR.
    timeout = httpx.Timeout(UPSTREAM_TIMEOUT_SECONDS, connect=10)
    with conversion_transport.observe() as response_received:
        with pdf_path.open("rb") as source:
            try:
                async with httpx.AsyncClient(timeout=timeout) as client:
                    async with client.stream("POST", f"{UPSTREAM_URL}/v1/convert/file",
                            files=[(key, (None, value)) for key, value in fields]
                                  + [("files", (filename, source, content_type))],
                            headers={"accept-encoding": "identity"}) as response:
                        length = response.headers.get("content-length")
                        declared_bytes = None
                        length_state = "absent" if length is None else "invalid"
                        if length is not None and length.isascii() and length.isdecimal():
                            # Bound conversion and log size; never log a raw header.
                            significant_length = length.lstrip("0") or "0"
                            length_state = "out_of_range" if len(significant_length) > 20 else "valid"
                            if length_state == "valid":
                                declared_bytes = int(significant_length)
                        LOGGER.info("Payload upstream_headers declared_content_length_bytes=%s declared_content_length_state=%s maximum_response_bytes=%s upstream_status=%s",
                                    declared_bytes, length_state, MAXIMUM_RESPONSE_BYTES, response.status_code)
                        if length_state == "out_of_range" or (declared_bytes is not None and declared_bytes > MAXIMUM_RESPONSE_BYTES):
                            LOGGER.warning("Payload refusal stage=upstream_headers reason=declared_length declared_content_length_bytes=%s declared_content_length_state=%s maximum_response_bytes=%s",
                                           declared_bytes, length_state, MAXIMUM_RESPONSE_BYTES)
                            raise ResponseSizeLimitError("Docling declared an oversized response.")
                        try:
                            body = await spool_upstream(response.aiter_bytes(chunk_size=CHUNK_BYTES), workspace)
                        except ResponseSizeLimitError:
                            LOGGER.warning("Payload refusal stage=upstream_download reason=actual_length declared_content_length_bytes=%s declared_content_length_state=%s maximum_response_bytes=%s",
                                           declared_bytes, length_state, MAXIMUM_RESPONSE_BYTES)
                            raise
                        status_code = response.status_code
                        media_type = response.headers.get("content-type", "application/json")
                        success = response.is_success
                # Both the response stream AND client have actually closed.
                response_received(success)
                return body, status_code, media_type
            except httpx.HTTPError as exc:
                LOGGER.warning("Docling upstream conversion failed error_type=%s", type(exc).__name__)
                raise HTTPException(status_code=502, detail="Docling upstream conversion failed.") from exc
