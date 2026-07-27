from __future__ import annotations

import asyncio
import json
import logging
import os
import tempfile
from pathlib import Path
from typing import Any

import httpx
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse, Response
from starlette.datastructures import UploadFile

from .heading_hierarchy import HeadingHierarchyConfiguration
from .ocr_engine import RapidOcrRegionEngine
from .response_repair import repair_response_tables


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
RENDER_SCALE = float(os.getenv("SAAIA_DOCLING_TABLE_REPAIR_SCALE", "2.5"))
PADDING_POINTS = float(
    os.getenv("SAAIA_DOCLING_TABLE_REPAIR_PADDING_POINTS", "12")
)
MINIMUM_SCORE = float(
    os.getenv("SAAIA_DOCLING_TABLE_REPAIR_MINIMUM_SCORE", "0.5")
)
HEADING_HIERARCHY = HeadingHierarchyConfiguration.from_environment()
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
ocr_engine = RapidOcrRegionEngine()


@app.get("/health")
async def health() -> JSONResponse:
    try:
        async with httpx.AsyncClient(timeout=3) as client:
            response = await client.get(f"{UPSTREAM_URL}/health")
        upstream_ready = response.is_success
    except httpx.HTTPError:
        upstream_ready = False
    status = 200 if upstream_ready else 503
    return JSONResponse(
        {
            "status": "ok" if upstream_ready else "upstream_unavailable",
            "upstream": upstream_ready,
            "regionalTableRepair": REPAIR_ENABLED,
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
        fields = [
            (key, value)
            for key, value in received_fields
            if key not in CONTROL_FIELD_NAMES
        ]

        with tempfile.TemporaryDirectory(prefix="saaia-docling-") as directory:
            pdf_path = Path(directory) / filename
            await _copy_bounded(upload, pdf_path)
            upstream_response = await _forward_conversion(
                pdf_path,
                filename,
                upload.content_type or "application/pdf",
                fields,
            )
            if not upstream_response.is_success:
                return Response(
                    content=upstream_response.content,
                    status_code=upstream_response.status_code,
                    media_type=upstream_response.headers.get(
                        "content-type",
                        "application/json",
                    ),
                )
            try:
                payload: Any = upstream_response.json()
            except json.JSONDecodeError as exc:
                raise HTTPException(
                    status_code=502,
                    detail="Docling returned invalid JSON.",
                ) from exc
            if not isinstance(payload, dict):
                raise HTTPException(
                    status_code=502,
                    detail="Docling returned an invalid response envelope.",
                )
            if REPAIR_ENABLED and payload.get("status") == "success":
                summaries = await repair_response_tables(
                    pdf_path,
                    payload,
                    ocr_engine,
                    render_scale=RENDER_SCALE,
                    padding_points=PADDING_POINTS,
                    minimum_score=MINIMUM_SCORE,
                )
                applied = sum(
                    item.get("status") == "applied" for item in summaries
                )
                if summaries:
                    LOGGER.info(
                        "Regional table repair candidates=%s applied=%s",
                        len(summaries),
                        applied,
                    )
            return JSONResponse(payload)


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
) -> httpx.Response:
    return await asyncio.to_thread(
        _forward_conversion_sync,
        pdf_path,
        filename,
        content_type,
        fields,
    )


def _forward_conversion_sync(
    pdf_path: Path,
    filename: str,
    content_type: str,
    fields: list[tuple[str, str]],
) -> httpx.Response:
    timeout = httpx.Timeout(
        UPSTREAM_TIMEOUT_SECONDS,
        connect=10,
    )
    with pdf_path.open("rb") as source:
        with httpx.Client(timeout=timeout) as client:
            try:
                return client.post(
                    f"{UPSTREAM_URL}/v1/convert/file",
                    files=[
                        (key, (None, value))
                        for key, value in fields
                    ] + [
                        (
                            "files",
                            (
                            filename,
                            source,
                            content_type,
                            ),
                        )
                    ],
                )
            except httpx.HTTPError as exc:
                LOGGER.exception("Docling upstream conversion failed")
                raise HTTPException(
                    status_code=502,
                    detail="Docling upstream conversion failed.",
                ) from exc
