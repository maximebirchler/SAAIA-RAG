"""Read-only tested-source / working-tree / Git-index byte anchoring.

The verifier never stages, commits, checks out, executes hooks or writes source
bytes. Only an explicit inventory and optional declared file lists are read.
EOL-only classification is diagnostic, never sufficient for strict equality.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
from typing import Any

ROLES = {"source", "test_source", "configuration", "evidence", "evidence_tool"}
CODE = {".cs", ".py", ".ps1", ".psm1", ".sql", ".sh"}
CONFIG = {".json", ".props", ".targets", ".csproj", ".sln", ".toml", ".yml", ".yaml", ".xml", ".config", ".xaml", ".resx"}
OID = re.compile(rb"(?:[a-f0-9]{40}|[a-f0-9]{64})")
MAX_BLOB_BYTES = 256 * 1024 * 1024


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def relative_path(value: str) -> str:
    if not isinstance(value, str): raise ValueError("A source path must be a relative string.")
    value = value.replace("\\", "/")
    if (not value or value.startswith("/") or ":" in value
            or any(part in {"", ".", ".."} for part in value.split("/"))):
        raise ValueError("Absolute paths and parent traversal are forbidden.")
    return value


def check_sha(value: Any) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[A-Fa-f0-9]{64}", value):
        raise ValueError("Expected a SHA256 digest.")
    return value.lower()


def git(repo: Path, args: list[str], data: bytes | None = None) -> bytes:
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("GIT_")}
    env.update({"GIT_OPTIONAL_LOCKS": "0", "GIT_TERMINAL_PROMPT": "0", "GIT_NO_REPLACE_OBJECTS": "1",
                "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
                "GIT_NO_LAZY_FETCH": "1", "GIT_ALLOW_PROTOCOL": ""})
    try:
        result = subprocess.run(["git", "-c", "core.fsmonitor=false", "-C", str(repo), *args],
            input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, timeout=120, check=False)
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError("Read-only Git command exceeded its timeout.") from exc
    if result.returncode != 0:
        raise RuntimeError(f"Read-only Git command failed: exit={result.returncode}; stderrSha256={sha(result.stderr)}")
    return result.stdout


def index_entries(repo: Path) -> dict[str, list[dict]]:
    entries: dict[str, list[dict]] = {}
    for record in git(repo, ["ls-files", "--stage", "-z"]).split(b"\0"):
        if not record: continue
        metadata, raw_path = record.split(b"\t", 1)
        mode, oid, stage = metadata.split(b" ")
        if not OID.fullmatch(oid): raise ValueError("Unexpected index object identity.")
        path = relative_path(raw_path.decode("utf-8"))
        entries.setdefault(path, []).append({"mode": mode.decode("ascii"), "oid": oid.decode("ascii"), "stage": int(stage)})
    return entries


def read_blobs(repo: Path, object_ids: list[str]) -> dict[str, bytes]:
    if not object_ids: return {}
    data = ("\n".join(sorted(set(object_ids))) + "\n").encode("ascii")
    sizes = git(repo, ["cat-file", "--batch-check"], data).splitlines()
    total = 0
    for row in sizes:
        parts = row.split()
        if len(parts) != 3 or parts[1] != b"blob": raise ValueError("An index entry is not an available blob.")
        total += int(parts[2])
    if len(sizes) != len(set(object_ids)) or total > MAX_BLOB_BYTES:
        raise ValueError("Selected Git blobs exceed the bounded read budget or are incomplete.")
    raw = git(repo, ["cat-file", "--batch"], data)
    blobs: dict[str, bytes] = {}; offset = 0
    while offset < len(raw):
        end = raw.index(b"\n", offset)
        header = raw[offset:end].split()
        if len(header) != 3 or not OID.fullmatch(header[0]) or header[1] != b"blob":
            raise ValueError("Malformed binary Git batch response.")
        size = int(header[2]); start = end + 1; stop = start + size
        if stop >= len(raw) or raw[stop:stop + 1] != b"\n": raise ValueError("Truncated binary Git blob.")
        blobs[header[0].decode("ascii")] = raw[start:stop]; offset = stop + 1
    if set(blobs) != set(object_ids): raise ValueError("Missing selected Git blobs.")
    return blobs


def eol_forms(data: bytes) -> tuple[bytes, bytes]:
    # Bare CR is preserved. This only recognizes a uniform CRLF/LF conversion.
    lf = data.replace(b"\r\n", b"\n")
    return lf, lf.replace(b"\n", b"\r\n")


def versus_hash(data: bytes | None, expected: str | None, peer: bytes | None = None) -> str:
    if data is None: return "MISSING"
    if expected is None: return "NO_TESTED_BASELINE"
    if sha(data) == expected: return "EXACT"
    if expected in {sha(form) for form in eol_forms(data)}: return "EOL_ONLY_PROVEN"
    if peer is not None and sha(peer) == expected and eol_forms(data)[0] == eol_forms(peer)[0]:
        return "EOL_ONLY_PROVEN"
    return "DIFFERENT_BYTES_NOT_EXPLAINED_BY_UNIFORM_EOL"


def versus_bytes(current: bytes | None, indexed: bytes | None) -> str:
    if current is None or indexed is None: return "MISSING"
    if current == indexed: return "EXACT"
    if eol_forms(current)[0] == eol_forms(indexed)[0]: return "EOL_ONLY_PROVEN"
    return "REAL_BYTE_DIVERGENCE"


def role_of(path: str) -> str:
    suffix = Path(path).suffix.lower(); parts = path.lower().split("/")
    if parts[0] == "artifacts": return "evidence_tool" if suffix in CODE else "evidence"
    if any(part.endswith(".tests") or part in {"test", "tests"} for part in parts) or Path(path).name.lower().startswith(("test_", "test-")):
        return "test_source"
    if suffix in CODE: return "source"
    if suffix in CONFIG or Path(path).name in {".gitattributes", ".gitignore", ".editorconfig"}: return "configuration"
    return "configuration"


def current_bytes(repo: Path, path: str) -> bytes | None:
    candidate = repo / path
    if not candidate.resolve().is_relative_to(repo): raise ValueError("A selected path resolves outside the repository.")
    if candidate.is_symlink(): raise ValueError("Selected source symlinks cannot be byte-anchored as regular files.")
    return candidate.read_bytes() if candidate.is_file() else None


def entries_digest(entries: dict) -> str:
    return sha(json.dumps(entries, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8"))


def verify(repo: Path, inventory_path: Path, expected_fingerprint: str, declared_paths: list[Path] | None = None) -> dict:
    repo = repo.resolve()
    actual_repo = Path(git(repo, ["rev-parse", "--show-toplevel"]).decode("utf-8").strip()).resolve()
    if actual_repo != repo: raise ValueError("The explicit repo must be the repository root.")
    inventory_raw = inventory_path.read_bytes()
    inventory = json.loads(inventory_raw.decode("utf-8-sig"))
    if not isinstance(inventory, list) or not inventory: raise ValueError("A nonempty source inventory array is required.")
    compact = json.dumps(inventory, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    fingerprint = sha(compact)
    if fingerprint != check_sha(expected_fingerprint): raise ValueError("Run source inventory fingerprint mismatch.")
    selected: dict[str, dict] = {}
    for item in inventory:
        if not isinstance(item, dict) or set(item) != {"path", "sha256"}: raise ValueError("Unexpected inventory fields.")
        path = relative_path(item["path"])
        if path in selected: raise ValueError("Duplicate tested inventory path.")
        selected[path] = {"testedSha256": check_sha(item["sha256"]), "declaredSha256": None, "role": role_of(path)}
    declared_provenance = []
    for declared_path in declared_paths or []:
        raw = declared_path.read_bytes(); payload = json.loads(raw.decode("utf-8-sig"))
        items = payload if isinstance(payload, list) else payload.get("files")
        if not isinstance(items, list): raise ValueError("A declared list needs an array or files array.")
        declared_provenance.append({"path": str(declared_path), "sha256": sha(raw)})
        for item in items:
            path = relative_path(item["path"]); expected = check_sha(item["sha256"])
            role = item.get("role", role_of(path))
            if role not in ROLES: raise ValueError("Unknown declared file role.")
            record = selected.setdefault(path, {"testedSha256": None, "declaredSha256": None, "role": role})
            if record["declaredSha256"] not in {None, expected}: raise ValueError("Conflicting declared hashes.")
            record["declaredSha256"] = expected
            # A declaration may classify a path, but never overwrite the tested baseline.
            record["role"] = role
    entries = index_entries(repo)
    chosen = [entry[0]["oid"] for path in selected if (entry := entries.get(path))
        and len(entry) == 1 and entry[0]["stage"] == 0 and entry[0]["mode"] in {"100644", "100755"}]
    blobs = read_blobs(repo, chosen)
    results = []
    for path, baseline in sorted(selected.items()):
        current = current_bytes(repo, path)
        indexed_entries = entries.get(path, [])
        if not indexed_entries: index_state = "ABSENT_FROM_INDEX"; entry = None
        elif len(indexed_entries) != 1 or indexed_entries[0]["stage"] != 0: index_state = "UNMERGED_INDEX"; entry = None
        elif indexed_entries[0]["mode"] not in {"100644", "100755"}: index_state = "UNSUPPORTED_INDEX_MODE"; entry = None
        else: index_state = "PRESENT"; entry = indexed_entries[0]
        indexed = blobs[entry["oid"]] if entry else None
        expected = baseline["testedSha256"] or baseline["declaredSha256"]
        current_tested = versus_hash(current, baseline["testedSha256"], indexed)
        index_tested = versus_hash(indexed, baseline["testedSha256"], current)
        current_expected = versus_hash(current, expected, indexed); index_expected = versus_hash(indexed, expected, current)
        index_current = versus_bytes(current, indexed)
        conflict = (baseline["testedSha256"] is not None and baseline["declaredSha256"] is not None
            and baseline["testedSha256"] != baseline["declaredSha256"])
        exact = index_state == "PRESENT" and current_expected == index_expected == index_current == "EXACT" and not conflict
        changed = baseline["testedSha256"] is not None and current_tested != "EXACT"
        results.append({"path": path, **baseline, "inventoryMember": baseline["testedSha256"] is not None,
            "workingTreeSha256": sha(current) if current is not None else None,
            "indexSha256": sha(indexed) if indexed is not None else None, "indexState": index_state,
            "indexEntry": entry, "workingTreeVsTested": current_tested, "indexVsTested": index_tested,
            "workingTreeVsExpected": current_expected, "indexVsExpected": index_expected,
            "indexVsWorkingTree": index_current, "testedFileChangedAfterRun": changed,
            "testCodeChangedAfterRun": (baseline["role"] == "test_source" or role_of(path) == "test_source") and changed,
            "declaredBaselineConflictsWithTested": conflict, "strictExact": exact,
            "provenance": "TESTED_INVENTORY" if baseline["testedSha256"] else "DECLARED_ONLY_NOT_CLAIMED_TESTED"})
    index_after = index_entries(repo)
    index_before_sha = entries_digest(entries); index_after_sha = entries_digest(index_after)
    for item in results:
        latest = current_bytes(repo, item["path"])
        latest_sha = sha(latest) if latest is not None else None
        item["workingTreeAfterSha256"] = latest_sha
        item["workingTreeChangedDuringVerification"] = latest_sha != item["workingTreeSha256"]
        if item["workingTreeChangedDuringVerification"]:
            item["strictExact"] = False
            if item["testedSha256"] is not None and latest_sha != item["testedSha256"]:
                item["testedFileChangedAfterRun"] = True
                item["testCodeChangedAfterRun"] = item["role"] == "test_source" or role_of(item["path"]) == "test_source"
    inventory_stable = inventory_path.is_file() and sha(inventory_path.read_bytes()) == sha(inventory_raw)
    declared_stable = all(Path(item["path"]).is_file() and sha(Path(item["path"]).read_bytes()) == item["sha256"]
        for item in declared_provenance)
    stable = index_before_sha == index_after_sha and inventory_stable and declared_stable
    role_counts = {role: sum(item["role"] == role for item in results) for role in sorted(ROLES)}
    return {"schemaVersion": "saaia-tested-source-index-anchor.v1", "strictPassed": stable and all(item["strictExact"] for item in results),
        "indexEntriesBeforeSha256": index_before_sha, "indexEntriesAfterSha256": index_after_sha,
        "indexChangedDuringVerification": index_before_sha != index_after_sha,
        "workingTreeChangedDuringVerificationCount": sum(item["workingTreeChangedDuringVerification"] for item in results),
        "inventoryInputStableDuringVerification": inventory_stable, "declaredInputsStableDuringVerification": declared_stable,
        "repo": str(repo), "inventoryPath": str(inventory_path), "inventoryFileSha256": sha(inventory_raw),
        "sourceFingerprint": fingerprint, "sourceFingerprintMatchesRun": True, "declaredLists": declared_provenance,
        "selectedFileCount": len(results), "roleCounts": role_counts,
        "testedFilesChangedAfterRun": sum(item["testedFileChangedAfterRun"] for item in results),
        "testCodeFilesChangedAfterRun": sum(item["testCodeChangedAfterRun"] for item in results),
        "absentFromIndex": sum(item["indexState"] == "ABSENT_FROM_INDEX" for item in results),
        "strictExactCount": sum(item["strictExact"] for item in results), "gitMutationCommandsExecuted": 0,
        "sourceContentsIncluded": False, "files": results,
        "limits": ["Only enumerated paths are checked; this does not claim the whole Git index is reviewed.",
            "Inventory hashes alone do not reconstruct arbitrary mixed-EOL historical bytes. EOL-only is proven only by an exact candidate-hash match.",
            "EOL-only is diagnostic and fails strict equality; source literals/generators may depend on physical line endings.",
            "Declared-only evidence/configuration is compared to its declared bytes, never asserted to have been tested.",
            "No source contents, secret values or raw Git stderr are included.",
            "A concurrent edit/staging operation can invalidate this point-in-time observation; run under a frozen source/index window."]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--inventory", type=Path, required=True)
    parser.add_argument("--expected-source-fingerprint", required=True)
    parser.add_argument("--declared", type=Path, action="append", default=[])
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = verify(args.repo, args.inventory, args.expected_source_fingerprint, args.declared)
    with args.output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2); stream.write("\n")
    print(json.dumps({"strictPassed": report["strictPassed"], "selectedFileCount": report["selectedFileCount"],
        "strictExactCount": report["strictExactCount"], "testedFilesChangedAfterRun": report["testedFilesChangedAfterRun"],
        "testCodeFilesChangedAfterRun": report["testCodeFilesChangedAfterRun"], "absentFromIndex": report["absentFromIndex"],
        "gitMutationCommandsExecuted": 0}))
    return 0 if report["strictPassed"] else 1


if __name__ == "__main__": raise SystemExit(main())
