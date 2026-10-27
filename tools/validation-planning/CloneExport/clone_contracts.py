"""Closed documentary SQL contract. Importing performs no I/O or subprocess work."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "validation-ingestion"))
from vector_point_identity import vector_point_id

TENANT = "77777777-7777-4777-8777-777777777777"
TABLES = (
    "documents", "ingestion_jobs", "document_revisions", "document_sections", "document_units", "retrieval_chunks",
    "exact_match_entries", "contextual_text_entries", "retrieval_chunk_links", "document_profiles",
    "document_profile_content_cards", "document_title_anchors", "document_navigation_entries", "document_profile_search_entries",
    "document_revision_artifacts", "document_page_index", "document_processing_runs", "document_revision_binary_artifacts",
    "document_source_anchors", "document_revision_visual_assets", "document_revision_table_observation_catalog",
    "document_revision_table_observations", "document_revision_native_raw_catalog", "document_revision_native_raw_pages",
    "document_summaries", "documents_category_nodes", "documents_catalog_summary", "documents_catalog_categories",
    "documents_catalog_category_aliases", "ingestion_generation_watermarks", "ingestion_vector_plans", "ingestion_vector_plan_points",
)
SPECIAL_TABLES = {"ingestion_vector_plans", "ingestion_vector_plan_points"}
DUMP_TABLES = tuple(table for table in TABLES if table not in SPECIAL_TABLES)
SNAPSHOT_PATTERN = r"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{8}-[1-9][0-9]*"
SESSION_SETTINGS = """SET LOCAL client_encoding='UTF8';
SET LOCAL timezone='UTC';
SET LOCAL datestyle='ISO, YMD';
SET LOCAL intervalstyle='postgres';
SET LOCAL bytea_output='hex';
SET LOCAL extra_float_digits=3;
SET LOCAL statement_timeout='300s';
SET LOCAL idle_in_transaction_session_timeout='0';
"""


def require(value, reason):
    if not value:
        raise ValueError(reason)


def table_name(value):
    require(value in TABLES, "table_outside_documentary_allowlist")
    return 'public."' + value + '"'


def snapshot_transaction(snapshot, query):
    require(isinstance(snapshot, str) and re.fullmatch(SNAPSHOT_PATTERN, snapshot), "invalid_exported_snapshot")
    return ("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\nSET TRANSACTION SNAPSHOT '" + snapshot + "';\n"
            + SESSION_SETTINGS + query + ";\nROLLBACK;\n")


def validate_schema(value):
    require(value.get("serverMajor") == 16 and value.get("encoding") == "UTF8", "postgres16_utf8_required")
    tables = value.get("tables", [])
    require(len(tables) == len(TABLES) and {row["name"] for row in tables} == set(TABLES), "schema_table_inventory_changed")
    require(value.get("migrations") and all(isinstance(row, str) for row in value["migrations"]), "schema_migrations_missing")
    result = {}
    for table in tables:
        columns, keys = table.get("columns", []), table.get("primaryKey", [])
        names = [column["name"] for column in columns]
        require(names and len(set(names)) == len(names) and keys and len(set(keys)) == len(keys)
                and all(key in names for key in keys), "schema_columns_or_primary_key_invalid")
        require(all(re.fullmatch(r"[a-z][a-z0-9_]*", name) for name in names), "unexpected_column_identifier")
        result[table["name"]] = table
    return result


def readback_query(table, descriptor):
    require(descriptor.get("name") == table, "readback_descriptor_table_mismatch")
    keys = descriptor.get("primaryKey", [])
    require(keys and all(re.fullmatch(r"[a-z][a-z0-9_]*", key) for key in keys), "readback_primary_key_invalid")
    # Ordering by UTF8 hex is locale-independent, including composite text PKs.
    order = ",".join("encode(convert_to(to_jsonb(t.\"" + key + "\")::text,'UTF8'),'hex') COLLATE \"C\"" for key in keys)
    return "SELECT to_jsonb(t)::text FROM " + table_name(table) + " t ORDER BY " + order


def validate_observation(value, run_id):
    require(value.get("schemaVersion") == "saaia-planning-worker-corpus-observation.v1"
            and value.get("ownerRunId") == run_id and value.get("canonicalRowsSeeded") is False
            and value.get("advancedJobCount") == value.get("activeJobCount") == 0,
            "fresh_real_worker_observation_required")
    docs = value.get("documents", [])
    require(len(docs) == 4 and len({d["docId"] for d in docs}) == len({d["revisionId"] for d in docs}) == 4
            and sum(d["pageCount"] for d in docs) == 510, "four_full_originals_required")
    for doc in docs:
        require(doc["tenantId"] == TENANT and doc["status"] == "indexed" and doc["pageCount"] > 0
                and doc["indexedVersion"] == doc["ingestionVersion"] > 0
                and doc["ingestionJob"]["status"] == "done", "worker_document_not_published")
        uuid.UUID(doc["docId"]); uuid.UUID(doc["revisionId"])
        require(re.fullmatch(r"[a-f0-9]{64}", doc["sourceSha256"]), "worker_source_hash_invalid")
    return docs


def validate_scope(rows, expected):
    require(rows.get("activeJobs") == rows.get("advancedJobs") == 0, "source_is_not_quiescent")
    current = rows.get("documents", [])
    keys = ("tenantId", "docId", "revisionId", "docPath", "sourceSha256", "pageCount", "ingestionVersion", "indexedVersion", "status")
    identity = lambda doc: tuple(doc[key] for key in keys)
    require(len(current) == 4 and sorted(map(identity, current)) == sorted(map(identity, expected)), "live_source_differs_from_worker")
    require(set(rows.get("tableTenantCounts", {})) == set(TABLES) - {"ingestion_vector_plan_points"}
            and all(count == 0 for count in rows["tableTenantCounts"].values()), "foreign_tenant_in_documentary_export")


def inspect_jsonl(path, descriptor):
    count, digest = 0, hashlib.sha256()
    pk_digest = hashlib.sha256()
    columns = {column["name"] for column in descriptor["columns"]}
    with path.open("rb") as source:
        for line in source:
            require(line.endswith(b"\n") and not line.endswith(b"\r\n"), "jsonl_transport_requires_lf")
            row = json.loads(line)
            require(isinstance(row, dict) and set(row) == columns, "jsonl_row_schema_changed")
            digest.update(line); count += 1
            pk = json.dumps([row[key] for key in descriptor["primaryKey"]], ensure_ascii=False, separators=(",", ":"))
            pk_digest.update(pk.encode("utf-8") + b"\n")
    return {"rowCount": count, "sha256": digest.hexdigest(), "primaryKeySequenceSha256": pk_digest.hexdigest()}


def validate_plan_rows(plans, points, documents, chunks):
    by_doc = {doc["docId"]: doc for doc in documents}
    require(len(plans) == len(by_doc) == 4 and len({p["plan_id"] for p in plans}) == 4
            and {p["doc_id"] for p in plans} == set(by_doc), "source_requires_four_current_vector_plans")
    plan_ids, observed_chunks = {p["plan_id"] for p in plans}, set()
    require(all(p["plan_id"] in plan_ids for p in points), "point_references_unregistered_plan")
    for plan in plans:
        doc = by_doc[plan["doc_id"]]
        require(plan["tenant_id"] == TENANT and plan["manifest_sealed"] is True and plan["retired_at"] is None
                and plan["generation"] == doc["indexedVersion"] and plan["plan_fingerprint"] == doc["activeVectorPlanFingerprint"],
                "source_vector_plan_not_current_sealed_publication")
        batch = [point for point in points if point["plan_id"] == plan["plan_id"]]
        require(len(batch) == plan["point_count"] > 0 and sorted(p["ordinal"] for p in batch) == list(range(len(batch)))
                and len({p["point_id"] for p in batch}) == len(batch), "source_vector_point_manifest_incomplete")
        for point in batch:
            chunk = chunks.get(point["canonical_chunk_id"])
            require(chunk and point["canonical_chunk_id"] not in observed_chunks
                    and chunk["revision_id"] == doc["revisionId"] and chunk["tenant_id"] == TENANT
                    and point["point_id"] == vector_point_id(point["canonical_chunk_id"], plan["plan_fingerprint"]),
                    "source_vector_chunk_identity_invalid")
            observed_chunks.add(point["canonical_chunk_id"])
    require(observed_chunks == set(chunks), "source_vector_chunk_inventory_incomplete")
