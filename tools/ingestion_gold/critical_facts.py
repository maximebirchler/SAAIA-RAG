"""Exact, page-bound facts: no fuzzy match, accent stripping or sign folding."""
from __future__ import annotations

import re
import unicodedata
from typing import Any


def exact_text(value: Any) -> str:
    return " ".join(unicodedata.normalize("NFC", str(value or "")).split())


def critical_fact_issues(specs: list[dict[str, Any]], pages: dict[int, list[str]],
                         retrieval: list[dict[str, Any]] | None = None) -> list[dict[str, str]]:
    issues: list[dict[str, str]] = []
    ids: set[str] = set()
    for spec in specs:
        if not isinstance(spec, dict):
            raise ValueError("Critical fact annotations must be objects.")
        fact_id = spec.get("id")
        page = spec.get("page")
        text = exact_text(spec.get("text"))
        if (not isinstance(fact_id, str) or not fact_id or fact_id in ids
                or type(page) is not int or page < 1 or not text):
            raise ValueError("Critical facts need unique IDs, a positive physical page and nonempty exact text.")
        ids.add(fact_id)
        # Word boundaries avoid accepting a small quantity inside a longer one.
        pattern = re.compile(r"(?<!\w)" + re.escape(text) + r"(?!\w)")
        source = exact_text(" ".join(pages.get(page, [])))
        if not pattern.search(source):
            issues.append({"check": "critical_fact", "message": f"Critical fact {fact_id} is absent or changed on physical page {page}."})
        if retrieval is not None and not any(
            int(chunk.get("pageStart", 0)) <= page <= int(chunk.get("pageEnd", 0))
            and pattern.search(exact_text(chunk.get("text"))) for chunk in retrieval
        ):
            issues.append({"check": "critical_retrieval_fact", "message": f"Critical fact {fact_id} is absent or changed in retrieval for physical page {page}."})
    return issues
