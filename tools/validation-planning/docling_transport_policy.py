"""Keep the owned Docling transport cap equal to the signed backend cap.

This controls response transport only; it changes neither OCR quality nor RAM.
Admission must precede source staging so configuration drift cannot waste OCR.
"""
from __future__ import annotations

import re

ENVIRONMENT_NAME = "SAAIA_DOCLING_MAX_RESPONSE_BYTES"
DEFAULT_BYTES = 512 * 1024**2
MIN_BYTES, MAX_BYTES = 1024**2, 2 * 1024**3


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def signed_limit(config):
    section = config.get("DocumentIntelligence", {})
    require(type(section) is dict, "docling_transport_config_shape_invalid")
    value = section.get("MaxResponseBytes", DEFAULT_BYTES)
    require(type(value) is int and MIN_BYTES <= value <= MAX_BYTES,
            "docling_transport_signed_limit_invalid")
    return value


def environment_limit(rows):
    require(type(rows) is list and all(type(row) is str and "=" in row for row in rows),
            "docling_transport_environment_shape_invalid")
    values = [row.split("=", 1)[1] for row in rows if row.startswith(ENVIRONMENT_NAME + "=")]
    require(len(values) <= 1, "docling_transport_environment_duplicate")
    if not values:
        return DEFAULT_BYTES
    require(re.fullmatch(r"[1-9][0-9]{0,9}", values[0]) is not None,
            "docling_transport_environment_value_invalid")
    value = int(values[0])
    require(MIN_BYTES <= value <= MAX_BYTES, "docling_transport_environment_value_invalid")
    return value


def validate_observed(config, container):
    expected = signed_limit(config)
    actual = environment_limit(container.get("Config", {}).get("Env"))
    require(actual == expected, "docling_transport_signed_sidecar_limit_mismatch")
    return {"signedBackendMaxResponseBytes": expected, "observedSidecarMaxResponseBytes": actual,
            "validated": True, "ocrQualityChanged": False, "memoryLimitChanged": False}


def configure_bootstrap(bootstrap):
    original_args, original_verify = bootstrap.container_args, bootstrap.verify_container

    def config(root, owner):
        path = bootstrap.child(root, "deploy/deployment.config.json")
        require(owner.get("configurationPath") == str(path), "docling_transport_configuration_path_changed")
        return bootstrap.read_json(path)

    def container_args(root, owner, role, image_id):
        arguments = original_args(root, owner, role, image_id)
        if role != "docling":
            return arguments
        expected = signed_limit(config(root, owner))
        existing = [arguments[i + 1] for i, value in enumerate(arguments[:-1]) if value in ("-e", "--env")]
        observed = environment_limit(existing)
        explicit = any(row.startswith(ENVIRONMENT_NAME + "=") for row in existing)
        if explicit:
            require(observed == expected, "docling_transport_arguments_conflict")
            return arguments
        indices = [i for i, value in enumerate(arguments) if value == image_id]
        require(len(indices) == 1, "docling_transport_image_argument_ambiguous")
        offset = indices[0]
        return arguments[:offset] + ["-e", ENVIRONMENT_NAME + "=" + str(expected)] + arguments[offset:]

    def verify_container(root, owner, row, docker, *, require_running=True):
        actual = original_verify(root, owner, row, docker, require_running=require_running)
        if row["role"] == "docling":
            validate_observed(config(root, owner), actual)
        return actual

    # A drifted transport setting must not defeat identity-checked cleanup.
    if not hasattr(bootstrap, "_docling_cleanup_identity_verifier"):
        bootstrap._docling_cleanup_identity_verifier = original_verify
    bootstrap.container_args, bootstrap.verify_container = container_args, verify_container
