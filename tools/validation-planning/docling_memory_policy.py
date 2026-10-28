"""Opt-in Docling scheduling policy for the owned 3 CPU / 7 GiB runtime.

This changes work in flight and redundant warm-up, never OCR resolution, images,
heading hierarchy, model weights, container memory or the signed backend config.
An environment observation is not proof of live pipeline options or fidelity.
"""
from __future__ import annotations

from copy import deepcopy

PROFILES = ("legacy", "cpu7g-bounded-v1")
POLICY_KEY = "doclingMemoryPolicy"
BOUNDED_ENVIRONMENT = {
    "DOCLING_SERVE_QUEUE_MAX_SIZE": "2",
    "DOCLING_SERVE_OCR_BATCH_SIZE": "1",
    "DOCLING_SERVE_LAYOUT_BATCH_SIZE": "1",
    "DOCLING_SERVE_TABLE_BATCH_SIZE": "1",
    "DOCLING_SERVE_LOAD_MODELS_AT_BOOT": "false",
    "DOCLING_SERVE_OPTIONS_CACHE_SIZE": "1",
}
BASELINE_EXECUTION_ENVIRONMENT = {
    "DOCLING_DEVICE": "cpu", "DOCLING_NUM_THREADS": "3", "OMP_NUM_THREADS": "3",
    "DOCLING_SERVE_ENG_KIND": "local", "DOCLING_SERVE_ENG_LOC_NUM_WORKERS": "1",
    "DOCLING_SERVE_ENG_LOC_SHARE_MODELS": "true",
}


def require(condition, code):
    if not condition:
        raise ValueError(code)


def policy(profile):
    require(type(profile) is str and profile in PROFILES, "reviewed_docling_memory_profile_required")
    if profile == "legacy":
        return None
    return {"schemaVersion": "saaia-owned-docling-memory-policy.v1", "profile": profile,
            "nanoCpus": 3_000_000_000, "memoryBytes": 7 * 1024**3,
            "environment": dict(BOUNDED_ENVIRONMENT), "qualitySettingsChanged": False,
            "containerResourcesChanged": False, "wholeDocumentMemoryBoundProven": False,
            "effectivePipelineSettingsObservedByEnvironmentValidation": False}


def exact(left, right):
    """JSON-like equality that never admits bool as an integer or unknown keys."""
    if type(left) is not type(right):
        return False
    if type(right) is dict:
        return left.keys() == right.keys() and all(exact(left[key], value) for key, value in right.items())
    if type(right) is list:
        return len(left) == len(right) and all(exact(a, b) for a, b in zip(left, right))
    return left == right


def owner_profile(owner):
    if POLICY_KEY not in owner:
        return "legacy"
    expected = policy("cpu7g-bounded-v1")
    require(exact(owner[POLICY_KEY], expected), "docling_memory_owner_policy_invalid")
    return expected["profile"]


def bind_owner(owner, profile):
    selected = policy(profile)
    require(POLICY_KEY not in owner, "docling_memory_owner_policy_already_present")
    if selected is not None:
        owner[POLICY_KEY] = deepcopy(selected)


def validate_phase(owner, origin, profile):
    selected = policy(profile)
    if selected is None:
        require(POLICY_KEY not in owner and POLICY_KEY not in origin,
                "docling_memory_profile_changed_between_phases")
    else:
        require(exact(owner.get(POLICY_KEY), selected) and exact(origin.get(POLICY_KEY), selected),
                "docling_memory_profile_changed_between_phases")


def environment_values(rows):
    require(type(rows) is list and len(rows) <= 1024 and all(type(row) is str and "=" in row for row in rows),
            "docling_memory_environment_shape_invalid")
    observed = {}
    for name in BOUNDED_ENVIRONMENT:
        values = [row.split("=", 1)[1] for row in rows if row.startswith(name + "=")]
        require(len(values) <= 1, "docling_memory_environment_duplicate")
        if values:
            observed[name] = values[0]
    config_files = [row.split("=", 1)[1] for row in rows if row.startswith("DOCLING_SERVE_CONFIG_FILE=")]
    require(not config_files, "docling_memory_external_config_unsupported")
    return observed


def validate_observed(owner, container):
    """After normal ownership inspection, admit the six exact Docker settings."""
    selected = policy(owner_profile(owner))
    if selected is None:
        return None
    rows = [row for row in owner.get("containers", []) if row.get("role") == "docling"]
    host = container.get("HostConfig", {})
    require(len(rows) == 1 and container.get("Id") == rows[0]["id"] and container.get("Image") == rows[0]["imageId"],
            "docling_memory_container_identity_changed")
    require(type(host.get("NanoCpus")) is int and host["NanoCpus"] == selected["nanoCpus"]
            and type(host.get("Memory")) is int and host["Memory"] == selected["memoryBytes"],
            "docling_memory_container_resources_changed")
    rows = container.get("Config", {}).get("Env")
    observed = environment_values(rows)
    require(exact(observed, selected["environment"]), "docling_memory_observed_environment_changed")
    execution = {}
    for name, expected in BASELINE_EXECUTION_ENVIRONMENT.items():
        values = [row.split("=", 1)[1] for row in rows if row.startswith(name + "=")]
        require(values == [expected], "docling_memory_execution_environment_changed")
        execution[name] = values[0]
    return {"profile": selected["profile"], "observedEnvironment": observed,
            "observedExecutionEnvironment": execution,
            "observedNanoCpus": host["NanoCpus"], "observedMemoryBytes": host["Memory"],
            "validated": True, "effectivePipelineSettingsObserved": False,
            "wholeDocumentMemoryBoundProven": False, "semanticFidelityAccepted": False}


def configure_bootstrap(bootstrap, profile):
    """Compose admission and argv; keep the pinned cleanup identity verifier.

    The pinned module ENV remains unchanged so its original identity verifier can
    still stop an owned container when one of the new settings is invalid.
    """
    selected = policy(profile)
    if selected is None:
        return None
    require(bootstrap.LIMITS.get("docling") == ("3", "7g"), "docling_memory_bootstrap_resource_baseline_changed")
    original_env = list(bootstrap.ENV["docling"])
    require(not environment_values(original_env), "docling_memory_bootstrap_already_overridden")
    require(all([row.split("=", 1)[1] for row in original_env if row.startswith(name + "=")] == [value]
                for name, value in BASELINE_EXECUTION_ENVIRONMENT.items()), "docling_memory_execution_baseline_changed")
    original_args, original_verify = bootstrap.container_args, bootstrap.verify_container

    def container_args(root, owner, role, image_id):
        arguments = original_args(root, owner, role, image_id)
        if role != "docling":
            return arguments
        require(exact(owner.get(POLICY_KEY), selected), "docling_memory_owner_policy_changed")
        require(bootstrap.LIMITS.get("docling") == ("3", "7g") and bootstrap.ENV["docling"] == original_env,
                "docling_memory_bootstrap_baseline_changed")
        indices = [index for index, value in enumerate(arguments) if value == image_id]
        existing = [arguments[index + 1] for index, value in enumerate(arguments[:-1]) if value in ("-e", "--env")]
        require(len(indices) == 1 and not environment_values(existing), "docling_memory_arguments_already_overridden")
        additions = [part for name, value in selected["environment"].items() for part in ("-e", name + "=" + value)]
        return arguments[:indices[0]] + additions + arguments[indices[0]:]

    def verify_container(root, owner, row, docker, *, require_running=True):
        actual = original_verify(root, owner, row, docker, require_running=require_running)
        if row["role"] == "docling":
            require(exact(owner.get(POLICY_KEY), selected), "docling_memory_owner_policy_changed")
            validate_observed(owner, actual)
        return actual

    bootstrap.container_args, bootstrap.verify_container = container_args, verify_container
    bootstrap._docling_cleanup_identity_verifier = original_verify
    return selected
