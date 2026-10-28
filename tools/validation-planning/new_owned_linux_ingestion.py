"""Thin opt-in CPU28 bootstrap adapter; only a NEW qualification UUID is mutable.

Reuses the pinned signed-Production v6 bootstrap and its owned cache-copy helper.
The source owner/images/model caches are observations, never restarted or edited.
No Docker, HTTP, credential or private configuration access without --execute.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import socket
import sys
from types import SimpleNamespace
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validation-ingestion"))
from linux_owned_runtime import OwnedDocker, assert_owned_path, validate_owner
import linux_planning_contracts as helpers
import docling_memory_policy as docling_memory

PARENT = Path("/home/maxime/saaia-ingestion-qualification")


def require(value, reason):
    if not value:
        raise ValueError(reason)


def digest_file(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def load_module(path, expected, name):
    require(digest_file(path) == expected, "pinned_bootstrap_or_cache_library_changed")
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def fresh_owner(root, port_base, subnet):
    run = uuid.UUID(root.name)
    require(root.is_absolute() and root.parent == PARENT and str(run) == root.name, "fresh_linux_uuid_root_required")
    assert_owned_path(root, root, allow_root=True)
    require(not root.exists() and 1024 <= port_base <= 65532 and 5432 not in range(port_base, port_base + 4), "fresh_root_and_distinct_high_ports_required")
    return {"schemaVersion": "saaia-linux-ingestion-runtime.v1", "runId": str(run), "root": str(root),
        "project": "saaiaqual_" + run.hex[:12], "network": "saaiaqual_" + run.hex[:12] + "_validation",
        "networkSubnet": subnet, "networkPolicy": "dedicated_bridge", "qualificationMode": "production", "admissionProfile": "manual",
        "containers": [], "database": "saaia_qual_" + run.hex[:12], "databaseUser": "saaia_qual_" + run.hex[:12],
        "qdrantCollection": "ingestion_" + run.hex, "configurationPath": str(root / "deploy/deployment.config.json"),
        "documentsRoot": str(root / "documents"), "state": "source_prepared",
        "endpoints": {name: "http://127.0.0.1:" + str(port_base + offset)
                      for offset, name in enumerate(("backend", "docling", "tei", "qdrant"))}}


def cache_inventory(root):
    require(root.is_dir() and not root.is_symlink(), "public_model_cache_missing_or_linked")
    result = {}
    for source in sorted(root.rglob("*")):
        if source.is_symlink():
            require(source.resolve().is_relative_to(root.resolve()) and source.is_file(), "cache_symlink_leaves_owned_source")
        if source.is_file():
            require(len(result) < 20000 and source.stat().st_size <= 8 * 1024**3, "cache_inventory_size_limit")
            result[source.relative_to(root).as_posix()] = {"bytes": source.stat().st_size, "sha256": digest_file(source)}
    require(result and any(name.startswith("tei/") for name in result) and any(name.startswith("docling/") for name in result), "complete_tei_and_docling_caches_required")
    return result


def assert_source_unchanged(source_path, source_bytes, docker):
    require(source_path.read_bytes() == source_bytes, "source_owner_changed")
    safe, observed = docker.inspect_all(require_running=False)
    require(not any(item["running"] for item in safe), "source_runtime_must_already_be_stopped")
    return observed


def stop_new_partial(bootstrap, root, docker):
    owner = bootstrap.read_json(root / "owner.json")
    observations = []
    for row in sorted(owner["containers"], key=lambda row: {"backend": 0, "docling": 1, "tei": 2, "qdrant": 3, "postgres": 4}[row["role"]]):
        # Resource admission must not prevent stopping a container whose exact
        # owner/image/mount/network identity still passes the pinned verifier.
        verify = getattr(bootstrap, "_" + row["role"] + "_cleanup_identity_verifier", bootstrap.verify_container)
        actual = verify(root, owner, row, docker, require_running=False)
        if actual["State"]["Running"]:
            docker.call("stop", "--time", "45", row["id"], timeout=60)
        actual = verify(root, owner, row, docker, require_running=False)
        require(actual["State"]["Running"] is False, "new_partial_runtime_stop_unconfirmed")
        observations.append({"role": row["role"], "id": row["id"], "stopped": True})
    bootstrap.new_file(root / "evidence/bootstrap-failure-owned-stop.json", bootstrap.json_bytes({"containers": observations, "dataDeleted": False}))


def configure_full_document_timeout(bootstrap, seconds):
    """Narrow, audited overlay of the hash-pinned v6 module's in-memory policy."""
    require(type(seconds) is int and seconds in (900, 3600), "reviewed_full_document_timeout_required")
    original = list(bootstrap.ENV["docling"])
    names = ("DOCLING_SERVE_MAX_SYNC_WAIT", "SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS")
    require(all(original.count(name + "=900") == 1 and
                sum(item.startswith(name + "=") for item in original) == 1 for name in names)
            and "TimeoutSeconds" not in bootstrap.REGIONAL_CONFIG, "pinned_v6_timeout_constants_changed")
    proxy_seconds = 900 if seconds == 900 else 3900
    if seconds == 3600:
        replacements = {name + "=900": name + "=" + str(proxy_seconds) for name in names}
        bootstrap.ENV["docling"] = [replacements.get(item,item) for item in original]
        bootstrap.REGIONAL_CONFIG = {**bootstrap.REGIONAL_CONFIG, "TimeoutSeconds": seconds}
    return {"schemaVersion": "saaia-owned-document-timeout-policy.v1", "profile":
            "cpu28_3cores_7GiB" if seconds == 900 else "cpu28_3cores_7GiB_document3600_proxy3900",
            "backendFullDocumentTimeoutSeconds": seconds, "doclingUpstreamTimeoutSeconds": proxy_seconds,
            "doclingMaximumSyncWaitSeconds": proxy_seconds, "regionalTextBudgetSeconds": 60,
            "proxyMarginSeconds": proxy_seconds - seconds, "default900Unchanged": seconds == 900,
            "maxSyncWaitPackageBoundVerifiedByThisAdapter": False, "cpuAndMemoryChanged": False}


def configure_backend_resources(bootstrap, memory_gib):
    """Only opt-in 3GiB changes the pinned module's in-memory Docker argv."""
    policy = helpers.backend_resource_policy(memory_gib)
    require(bootstrap.LIMITS.get("backend") == ("2", "2g") and bootstrap.LIMITS.get("docling") == ("3", "7g"),
            "pinned_backend_or_docling_resource_baseline_changed")
    if memory_gib == 2:
        return policy
    original_args, original_verify = bootstrap.container_args, bootstrap.verify_container
    bootstrap.LIMITS = {**bootstrap.LIMITS, "backend": ("2", "3g")}

    def container_args(root, owner, role, image_id):
        arguments = original_args(root, owner, role, image_id)
        if role != "backend":
            return arguments
        require(owner.get("backendResourcePolicy") == policy, "backend_resource_owner_policy_changed")
        indices = [index for index, value in enumerate(arguments) if value == image_id]
        require(len(indices) == 1 and "--memory-swap" not in arguments
                and arguments.count("--memory") == arguments.count("--cpus") == 1
                and arguments[arguments.index("--memory") + 1] == "3g"
                and arguments[arguments.index("--cpus") + 1] == "2", "pinned_backend_resource_arguments_changed")
        return arguments[:indices[0]] + ["--memory-swap", "3g"] + arguments[indices[0]:]

    def verify_container(root, owner, row, docker, *, require_running=True):
        actual = original_verify(root, owner, row, docker, require_running=require_running)
        if row["role"] == "backend":
            require(owner.get("backendResourcePolicy") == policy, "backend_resource_owner_policy_changed")
            helpers.validate_backend_resources(owner, actual)
        return actual

    bootstrap.container_args, bootstrap.verify_container = container_args, verify_container
    bootstrap._backend_cleanup_identity_verifier = original_verify
    return policy


def select_docling_image(args, source):
    image, revision = getattr(args, "docling_image", None), getattr(args, "docling_source_revision", None)
    require((image is None) == (revision is None), "docling_image_and_source_revision_pair_required")
    explicit = image is not None
    prepared = source["bootstrapPreparation"]
    if not explicit:
        image, revision = prepared["dependencyImages"]["docling"], prepared["doclingSourceRevision"]
    require(isinstance(image, str) and re.fullmatch("sha256:[a-f0-9]{64}", image)
            and isinstance(revision, str) and re.fullmatch("[a-f0-9]{64}", revision),
            "immutable_docling_image_and_source_required")
    return {"imageId": image, "sourceRevision": revision,
            "selection": "explicit_image" if explicit else "source_owner"}


def dependency_image_origin(source, selected):
    original = source["bootstrapPreparation"]["dependencyImages"]
    source_images = {row["role"]: row["imageId"] for row in source["containers"] if row["role"] != "backend"}
    require(set(original) == {"postgres", "qdrant", "tei", "docling"} and original == source_images,
            "source_dependency_images_do_not_match_owned_containers")
    actual = {**original, "docling": selected["imageId"]}
    return {"sourceDependencyImages": dict(original), "dependencyImages": actual,
            "dependencyImagesReused": {role: image for role, image in original.items() if actual[role] == image},
            "doclingImage": selected["imageId"], "doclingSourceRevision": selected["sourceRevision"],
            "doclingImageSelection": selected["selection"]}


def validate_docling_image_phase(owner, origin, source, selected):
    expected = dependency_image_origin(source, selected)
    record = owner.get("bootstrapPreparation", {})
    require(record.get("dependencyImages") == expected["dependencyImages"]
            and record.get("doclingSourceRevision") == selected["sourceRevision"],
            "planning_docling_preparation_identity_changed")
    if "doclingImageSelection" in origin or any(name in origin for name in
                                               ("sourceDependencyImages", "dependencyImages", "doclingImage", "doclingSourceRevision")):
        require(all(origin.get(name) == value for name, value in expected.items()), "planning_docling_origin_changed")
    else:
        # Historical origins only described reused dependencies. Admit them only
        # with the unchanged source-owner selection and matching prepared bytes.
        require(selected["selection"] == "source_owner"
                and origin.get("dependencyImagesReused") == expected["sourceDependencyImages"],
                "planning_docling_legacy_origin_changed")


def execute(args, source, source_bytes):
    selected_docling = select_docling_image(args, source)
    image_origin = dependency_image_origin(source, selected_docling)
    bootstrap = load_module(args.bootstrap, args.bootstrap_sha256, "planning_cpu28_bootstrap")
    timeout_policy = configure_full_document_timeout(bootstrap, args.full_document_timeout_seconds)
    resource_policy = configure_backend_resources(bootstrap, args.backend_memory_gib)
    memory_profile = getattr(args, "docling_memory_profile", "legacy")
    docling_policy = docling_memory.configure_bootstrap(bootstrap, memory_profile)
    require(bootstrap.LIMITS["docling"] == ("3", "7g") and bootstrap.REGIONAL_CONFIG["NumThreads"] == 3
            and bootstrap.REGIONAL_CONFIG["RegionalTextRepairEnabled"] is True, "pinned_cpu28_profile_required")
    root = args.root
    if args.phase == "prepare":
        owner = fresh_owner(root, args.port_base, args.network_subnet)
        owner["fullDocumentTimeoutPolicy"] = timeout_policy
        owner["backendResourcePolicy"] = resource_policy
        docling_memory.bind_owner(owner, memory_profile)
        source_docker = OwnedDocker(source, execute=True)
        assert_source_unchanged(args.source_owner, source_bytes, source_docker)
        source_root = Path(source["root"])
        inventory = cache_inventory(source_root / "cache")
        cache_bytes = sum(item["bytes"] for item in inventory.values())
        require(shutil.disk_usage(PARENT).free > cache_bytes + 2 * 1024**3, "insufficient_space_for_owned_cache_and_evidence")
        sockets = []
        try:
            for offset in range(4):
                handle = socket.socket(); sockets.append(handle)
                handle.bind(("127.0.0.1", args.port_base + offset))
        finally:
            for handle in sockets:
                handle.close()
        os.umask(0o077); root.mkdir(mode=0o700)
        for relative in ("evidence", "documents"):
            (root / relative).mkdir(mode=0o700)
        bootstrap.new_file(root / "owner.json", bootstrap.json_bytes(owner))
        docker = bootstrap.Docker(root)
        bootstrap.verify_source_image(docker, "backend", args.backend_image, args.backend_source_revision)
        bootstrap.verify_source_image(docker, "docling", selected_docling["imageId"], selected_docling["sourceRevision"])
        copied = load_module(args.cache_library, args.cache_library_sha256, "planning_owned_cache_copy")
        # Bind only the documented cache-copy function's input/output globals.
        # Its hardcoded old main()/inputs() and network preparation are never invoked.
        copied.ROOT, copied.CATALOG, copied.RUN = root, source_root, root.name
        copied.copy_cache(source["bootstrapPreparation"]["dependencyImages"]["docling"])
        require(cache_inventory(root / "cache") == inventory and cache_inventory(source_root / "cache") == inventory,
                "public_cache_exact_copy_or_source_stability_failed")
        bootstrap.new_file(root / "evidence/model-cache-copy.private.json", bootstrap.json_bytes({
            "sourceOwnerSha256": args.source_owner_sha256, "sourceRoot": str(source_root), "sourceReadOnly": True,
            "files": inventory, "bytes": cache_bytes, "exactCopy": True, "sourceUnchanged": True}))
        embedding = json.loads(args.source_embedding_profile.read_bytes())
        embedding["Rag"]["EmbeddingsBaseUrl"] = owner["endpoints"]["tei"]
        bootstrap.new_file(root / "embedding-profile.json", bootstrap.json_bytes(embedding))
        result = bootstrap.run(SimpleNamespace(root=root, catalog_root=source_root, admission_profile="manual",
            prepare_only=True, backend_image=None, docling_image_id=selected_docling["imageId"],
            expected_docling_source_revision=selected_docling["sourceRevision"],
            expected_backend_source_revision=args.backend_source_revision, network_subnet=args.network_subnet, execute=True))
        assert_source_unchanged(args.source_owner, source_bytes, source_docker)
        bootstrap.new_file(root / "evidence/planning-bootstrap-origin.json", bootstrap.json_bytes({
            "schemaVersion": "saaia-planning-linux-bootstrap-origin.v1", "sourceOwnerSha256": args.source_owner_sha256,
            "sourceRoot": source["root"], "sourceOwnerModified": False, "sourceContainersRestarted": False,
            "bootstrapSha256": args.bootstrap_sha256, "cacheLibrarySha256": args.cache_library_sha256,
            "sourceEmbeddingProfileSha256": args.source_embedding_profile_sha256,
            "embeddingProfileUrlRebasedOnly": True,
            "backendImage": args.backend_image, "backendSourceRevision": args.backend_source_revision,
            **image_origin, "cpuProfile": timeout_policy["profile"],
            "fullDocumentTimeoutPolicy": timeout_policy,
            "backendResourcePolicy": resource_policy,
            **({docling_memory.POLICY_KEY: docling_policy} if docling_policy is not None else {}),
            "paidProviderEnabled": False, "newIngestionPerformed": False}))
        return result
    require(root.is_absolute() and root.parent == PARENT and str(uuid.UUID(root.name)) == root.name, "exact_linux_uuid_root_required")
    assert_owned_path(root, root, allow_root=True)
    origin = bootstrap.read_json(root / "evidence/planning-bootstrap-origin.json")
    require(origin["sourceOwnerSha256"] == args.source_owner_sha256 and origin["bootstrapSha256"] == args.bootstrap_sha256
            and origin["backendImage"] == args.backend_image and origin["backendSourceRevision"] == args.backend_source_revision,
            "planning_bootstrap_origin_changed")
    require(origin.get("fullDocumentTimeoutPolicy", timeout_policy if args.full_document_timeout_seconds == 900 else None)
            == timeout_policy, "planning_full_document_timeout_policy_changed")
    prepared_owner = bootstrap.read_json(root / "owner.json")
    validate_docling_image_phase(prepared_owner, origin, source, selected_docling)
    helpers.validate_backend_resource_phase(prepared_owner, origin, resource_policy)
    docling_memory.validate_phase(prepared_owner, origin, memory_profile)
    docker = bootstrap.Docker(root)
    try:
        return bootstrap.run(SimpleNamespace(root=root, catalog_root=Path(source["root"]), admission_profile="manual",
            prepare_only=False, backend_image=args.backend_image if args.phase == "backend" else None,
            network_subnet=None, execute=True))
    except Exception:
        stop_new_partial(bootstrap, root, docker)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--phase", required=True, choices=("prepare", "dependencies", "backend"))
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--source-owner", required=True, type=Path)
    parser.add_argument("--source-owner-sha256", required=True)
    parser.add_argument("--bootstrap", required=True, type=Path)
    parser.add_argument("--bootstrap-sha256", required=True)
    parser.add_argument("--cache-library", required=True, type=Path)
    parser.add_argument("--cache-library-sha256", required=True)
    parser.add_argument("--source-embedding-profile", required=True, type=Path)
    parser.add_argument("--source-embedding-profile-sha256", required=True)
    parser.add_argument("--backend-image", required=True)
    parser.add_argument("--backend-source-revision", required=True)
    parser.add_argument("--docling-image", help="Optional exact image ID; requires --docling-source-revision. Default: source owner image.")
    parser.add_argument("--docling-source-revision", help="Optional 64 lowercase hex source identity; requires --docling-image.")
    parser.add_argument("--port-base", type=int, default=22521)
    parser.add_argument("--network-subnet", default="10.238.29.0/24")
    parser.add_argument("--full-document-timeout-seconds", type=int, choices=(900,3600), default=900)
    parser.add_argument("--backend-memory-gib", type=int, choices=(2,3), default=2)
    parser.add_argument("--docling-memory-profile", choices=docling_memory.PROFILES, default="legacy")
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    source_bytes = args.source_owner.read_bytes()
    require(hashlib.sha256(source_bytes).hexdigest() == args.source_owner_sha256, "source_owner_bytes_changed")
    source = validate_owner(json.loads(source_bytes))
    require(args.root != Path(source["root"]), "source_cannot_be_target")
    selected_docling = select_docling_image(args, source)
    for value in (args.backend_source_revision, args.bootstrap_sha256, args.cache_library_sha256):
        require(re.fullmatch("[a-f0-9]{64}", value), "exact_source_and_script_hashes_required")
    require(re.fullmatch("sha256:[a-f0-9]{64}", args.backend_image), "immutable_new_backend_image_required")
    require(args.backend_source_revision != source["bootstrapPreparation"]["backendSourceRevision"]
            and args.backend_image != next(r["imageId"] for r in source["containers"] if r["role"] == "backend"),
            "new_backend_required_after_product_fix")
    require(digest_file(args.bootstrap) == args.bootstrap_sha256 and digest_file(args.cache_library) == args.cache_library_sha256, "pinned_library_bytes_changed")
    require(digest_file(args.source_embedding_profile) == args.source_embedding_profile_sha256, "source_embedding_profile_changed")
    if not args.execute:
        if args.phase == "prepare":
            fresh_owner(args.root, args.port_base, args.network_subnet)
        print(json.dumps({"executeRequested": False, "phase": args.phase, "root": str(args.root), "sourceRoot": source["root"],
            "backendImage": args.backend_image, "cpuProfile": "cpu28_3cores_7GiB" if args.full_document_timeout_seconds == 900 else "cpu28_3cores_7GiB_document3600_proxy3900",
            "fullDocumentTimeoutSeconds": args.full_document_timeout_seconds, "privateFilesRead": False,
            "backendResourcePolicy": helpers.backend_resource_policy(args.backend_memory_gib),
            "doclingImage": selected_docling["imageId"], "doclingSourceRevision": selected_docling["sourceRevision"],
            "doclingImageSelection": selected_docling["selection"], "doclingImageVerified": False,
            **({docling_memory.POLICY_KEY: docling_memory.policy(args.docling_memory_profile)}
               if args.docling_memory_profile != "legacy" else {}),
            "dockerCalled": False, "dependenciesStarted": False, "providerCalls": 0}))
        return
    print(json.dumps(execute(args, source, source_bytes)))


if __name__ == "__main__":
    main()
