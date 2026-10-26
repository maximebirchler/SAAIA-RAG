"""Pure guards for the bounded invalid-PDF replacement qualification."""
from __future__ import annotations

import copy
import hashlib
from pathlib import Path
import uuid

from linux_lifecycle_checks import CASE_ID, DOC_PATH, digest, require, validate_points


def validate_lifecycle_proof(proof: dict, plan: dict) -> dict:
    require(proof.get("schemaVersion") == "saaia-linux-lifecycle-execution.v1"
            and proof.get("executed") is True and proof.get("passed") is True
            and proof.get("lateWriteReappearanceRemoved") is True
            and proof.get("generationsAndPhysicalPointIdsNeverReused") is True,
            "Replacement requires an actual successful lifecycle execution, not a dry-run or a mock.")
    for name in ("runId", "ownerManifestSha256", "corpusManifestSha256", "sourceSha256", "tenantId", "caseId"):
        require(proof.get("plan", {}).get(name) == plan.get(name), "Lifecycle proof changed owner, source or scope.")
    require(proof["plan"].get("caseId") == CASE_ID and proof.get("paidProviderCalls") == 0
            and proof.get("canonicalRowsSeeded") is False,
            "The lifecycle baseline is not the expected unseeded synthetic scope.")
    cleanup = proof.get("cleanup", {})
    require(all(cleanup.get(k) is True for k in ("zeroActiveJobs", "ownedContainersStopped",
            "dockerPublishedPortBindingsAbsent", "workingSourceRestored")), "Lifecycle cleanup was not validated.")
    published = [step for step in proof.get("steps", []) if step.get("searchToReadPassed") is True]
    require(bool(published), "The lifecycle proof lacks a verified final source-bound publication.")
    return copy.deepcopy(published[-1])


def bind_baseline(row: dict, prior: dict) -> None:
    for current, recorded in (("docId", "docId"), ("revisionId", "revisionId"), ("indexedVersion", "indexedVersion"),
                              ("ingestionVersion", "ingestionVersion"), ("plan", "planFingerprint"),
                              ("sourceSha256", "sourceSha256")):
        require(row.get(current) == prior.get(recorded), "The live baseline differs from the final lifecycle publication.")


def preserved_publication(snapshot: dict, before: dict, before_points: list, points: list, version: int) -> dict:
    row = snapshot.get("document") or {}
    for name in ("tenantId", "docId", "docPath", "indexedVersion", "revisionId", "sourceSha256", "plan", "chunks"):
        require(row.get(name) == before.get(name), "Invalid replacement modified the previously published source or chunks.")
    require(row.get("docPath") == DOC_PATH and row.get("ingestionVersion") == version
            and type(snapshot.get("watermark")) is int and snapshot["watermark"] >= version,
            "The failed replacement lost its distinct durable generation.")
    plans = [p for p in snapshot.get("plans", []) if p.get("generation") == before["indexedVersion"]
             and p.get("fingerprint") == before["plan"]]
    require(len(plans) == 1 and plans[0].get("manifestSealed") is True and plans[0].get("retiredAt") is None,
            "The protected current vector manifest was retired or disappeared.")
    validate_points(row, points)
    ordered = lambda items: sorted(items, key=lambda point: point["id"])
    require(digest(ordered(points)) == digest(ordered(before_points)),
            "Invalid replacement changed a protected physical vector or payload.")
    return row


def failed_terminal(terminal: dict, snapshot: dict, version: int, *, maximum_attempts=3) -> None:
    require(terminal.get("status") == "failed" and type(terminal.get("attempts")) is int
            and 1 <= terminal["attempts"] <= maximum_attempts and snapshot.get("activeJobs") == 0
            and snapshot.get("document", {}).get("ingestionVersion") == version,
            "Invalid PDF did not reach a bounded failed terminal with zero active jobs.")


class ControlledPdfCopy:
    """Modify only the known working copy; refuse to overwrite unexpected bytes."""
    def __init__(self, source: Path, target: Path, source_sha: str, scope_check):
        self.source, self.target, self.source_sha, self.scope_check = source, target, source_sha, scope_check
        self.expected_sha, self.mutated = source_sha, False

    @staticmethod
    def sha(path):
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def check(self):
        self.scope_check()
        require(self.source != self.target and self.source.is_file() and self.target.is_file()
                and self.sha(self.source) == self.source_sha and self.sha(self.target) == self.expected_sha,
                "The pinned source or controlled working PDF changed unexpectedly.")

    def replace_known(self, content: bytes):
        # Prepare completely before replacement. A failed write must not leave a
        # half-written working source that finally cannot identify safely.
        temporary = self.target.with_name(self.target.name + ".qualification-" + uuid.uuid4().hex)
        try:
            with temporary.open("xb") as output:
                output.write(content)
                output.flush()
            self.scope_check()
            temporary.replace(self.target)
        finally:
            if temporary.exists():
                temporary.unlink()

    def truncate(self):
        self.check()
        original = self.source.read_bytes()
        require(len(original) > 256 and original.startswith(b"%PDF-"), "The known synthetic source is not a full PDF.")
        truncated = original[:16]
        self.expected_sha = hashlib.sha256(truncated).hexdigest()
        # Mark before replacement so finally can restore after a later failure.
        self.mutated = True
        self.replace_known(truncated)
        self.check()
        return {"sourceSha256": self.source_sha, "truncatedSha256": self.expected_sha,
                "originalByteCount": len(original), "truncatedByteCount": len(truncated), "prefixOnly": True}

    def restore(self):
        self.scope_check()
        require(self.source.is_file() and self.sha(self.source) == self.source_sha,
                "The pinned restore source changed.")
        if self.target.exists():
            require(self.target.is_file() and self.sha(self.target) in {self.expected_sha, self.source_sha},
                    "Refuse to overwrite unknown working-copy bytes during restoration.")
        if not self.target.exists() or self.sha(self.target) != self.source_sha:
            self.replace_known(self.source.read_bytes())
        self.expected_sha = self.source_sha
        self.check()


def exercise_failure(lifecycle, controlled, before: dict, before_points: list, *, stable_seconds: int):
    """Run the failure phase only; caller must restore in finally and verify recovery."""
    require(type(stable_seconds) is int and 5 <= stable_seconds <= 60, "Use a bounded terminal stability window.")
    fault = controlled.truncate()
    lifecycle.capture("controlled-truncated-pdf", fault)
    ack = lifecycle.enqueue("upsert", before["ingestionVersion"])
    deadline = lifecycle.clock() + lifecycle.maximum_wait
    ids = [point["id"] for point in before_points]
    terminal = None
    while True:
        observed = lifecycle.job(ack["jobId"])
        snap = lifecycle.snapshot()
        preserved_publication(snap, before, before_points, lifecycle.points(ids, vectors=True), ack["version"])
        if observed["status"] not in {"queued", "running", "paused"}:
            failed_terminal(observed, snap, ack["version"])
            terminal = observed
            break
        require(lifecycle.clock() < deadline, "Invalid replacement remained active/retrying beyond its bounded deadline.")
        lifecycle.sleep(0.5)
    stable_until = lifecycle.clock() + stable_seconds
    while True:
        now = lifecycle.job(ack["jobId"])
        snap = lifecycle.snapshot()
        failed_terminal(now, snap, ack["version"])
        require(now == terminal, "The failed job restarted or changed attempts during the observation window.")
        preserved_publication(snap, before, before_points, lifecycle.points(ids, vectors=True), ack["version"])
        if lifecycle.clock() >= stable_until:
            break
        lifecycle.sleep(0.5)
    return {"phase": "invalid-replacement", "status": "PASS", "jobId": ack["jobId"], "generation": ack["version"],
            "attempts": terminal["attempts"], "terminal": "failed", "terminalStableSeconds": stable_seconds,
            "previousPublicationPreserved": True, "physicalVectorsAndPayloadsUnchanged": True,
            "currentSourceFreshnessClaimed": False, "unboundedRetryObserved": False,
            "retryObservationIsTimeBounded": True, "fault": fault}
