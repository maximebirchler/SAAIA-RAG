"""Pure, separate contracts for the native TEI and validation Transformers runtimes.

Declared expected identity is frozen before requests. Docker/model observations are
checked separately; a TEI /info response is never made to impersonate our adapter.
"""
from __future__ import annotations
import re

DIMENSIONS = 768
MODEL_ID = "intfloat/multilingual-e5-base"

def validate_expected_profile(profile: dict | None, embedding: str) -> str:
    if not isinstance(profile, dict):
        raise ValueError("An independent frozen embedding profile is required.")
    rag = profile.get("Rag", {})
    if (not isinstance(rag, dict) or rag.get("EmbeddingsBaseUrl", "").rstrip("/") != embedding
            or rag.get("EmbeddingsModel") != MODEL_ID
            or not re.fullmatch(r"[a-f0-9]{40}(?:[a-f0-9]{24})?", str(rag.get("EmbeddingsModelRevision", "")))
            or not re.fullmatch(r"[a-f0-9]{64}", str(profile.get("modelFileSha256", "")))
            or not isinstance(rag.get("EmbeddingsRuntimeRevision"), str) or not rag["EmbeddingsRuntimeRevision"]):
        raise ValueError("Malformed immutable E5 embedding profile.")
    adapter = profile.get("adapter")
    if adapter == "transformers_cpu" and profile.get("schemaVersion") == "saaia-local-embedding-profile.v1":
        return adapter
    if adapter != "tei_cpu" or profile.get("schemaVersion") != "saaia-tei-embedding-profile.v1":
        raise ValueError("Unknown embedding runtime contract.")
    expected = profile.get("expectedInfo")
    if (profile.get("dimensions") != DIMENSIONS or not isinstance(expected, dict)
            or expected.get("model_id") != MODEL_ID
            or not isinstance(expected.get("version"), str) or not expected["version"]
            or type(expected.get("max_input_length")) is not int or not 1 <= expected["max_input_length"] <= 65536
            or not re.fullmatch(r"sha256:[a-f0-9]{64}", str(profile.get("imageId", "")))
            or rag["EmbeddingsRuntimeRevision"] != profile["imageId"]
            or profile.get("truncationPolicy") not in {"reject", "auto_truncate"}):
        raise ValueError("TEI requires frozen native info, image, dimensions and explicit truncation policy.")
    revision_method = profile.get("modelRevisionAttestation", "native_info")
    if revision_method not in {"native_info", "owned_command_and_weights"}:
        raise ValueError("Unknown TEI model revision attestation method.")
    if (expected.get("model_sha") != rag["EmbeddingsModelRevision"]
            and not (expected.get("model_sha") is None and "model_sha" in expected
                     and revision_method == "owned_command_and_weights")):
        raise ValueError("A null native model SHA needs separately owned command and weight evidence.")
    artifacts = profile.get("modelArtifacts")
    weight_path = profile.get("modelFileRelativePath")
    if artifacts is not None:
        from pathlib import PurePosixPath
        if (not isinstance(artifacts, dict) or not artifacts or weight_path not in artifacts
                or artifacts[weight_path] != profile["modelFileSha256"]):
            raise ValueError("TEI model artifact inventory must include the exact executed weights.")
        for relative, digest in artifacts.items():
            path = PurePosixPath(relative)
            if path.is_absolute() or ".." in path.parts or not re.fullmatch(r"[a-f0-9]{64}", str(digest)):
                raise ValueError("TEI model artifacts require safe relative paths and exact hashes.")
    return adapter

def attest_info(info: dict, profile: dict | None, embedding: str) -> None:
    adapter = validate_expected_profile(profile, embedding)
    if not isinstance(info, dict):
        raise ValueError("Native embedding info must be a JSON object.")
    rag = profile["Rag"]
    if adapter == "tei_cpu":
        if any(info.get(key) != value for key, value in profile["expectedInfo"].items()):
            raise ValueError("Observed native TEI info differs from the frozen profile.")
        return
    expected = {"adapter": adapter, "model_id": rag["EmbeddingsModel"],
                "model_revision": rag["EmbeddingsModelRevision"], "model_file_sha256": profile["modelFileSha256"],
                "runtime_revision": rag["EmbeddingsRuntimeRevision"]}
    if (info.get("offline") is not True or type(info.get("dimensions")) is not int or info["dimensions"] != DIMENSIONS
            or any(info.get(key) != value for key, value in expected.items())):
        raise ValueError("Observed embedding runtime differs from the frozen profile.")

def attest_tei_container(profile: dict, observed: dict) -> None:
    """No Docker execution here: the owned runner supplies separately captured facts."""
    if validate_expected_profile(profile, profile["Rag"]["EmbeddingsBaseUrl"].rstrip("/")) != "tei_cpu":
        raise ValueError("A TEI profile is required.")
    if (observed.get("imageId") != profile["imageId"]
            or observed.get("modelFileSha256") != profile["modelFileSha256"]
            or observed.get("modelRevision") != profile["Rag"]["EmbeddingsModelRevision"]
            or observed.get("autoTruncate") is not (profile["truncationPolicy"] == "auto_truncate")
            or observed.get("ownedContainerVerified") is not True):
        raise ValueError("TEI container/model/truncation observations do not match the frozen profile.")


def tei_model_binding(profile: dict, container: dict) -> dict:
    """Resolve the pinned snapshot through actual container argv and bind mounts.

    This is a pure mapping check. The runner separately checks symlinks, file
    hashes and unchanged artifacts before and after the real worker run.
    """
    from pathlib import PurePosixPath
    validate_expected_profile(profile, profile["Rag"]["EmbeddingsBaseUrl"].rstrip("/"))
    command = container.get("Config", {}).get("Cmd", [])
    def option(name: str) -> str:
        if command.count(name) != 1 or command.index(name) + 1 >= len(command):
            raise ValueError("TEI needs an explicit, unambiguous model, revision and cache path.")
        return command[command.index(name) + 1]
    cache = PurePosixPath(option("--huggingface-hub-cache"))
    if not cache.is_absolute() or ".." in cache.parts:
        raise ValueError("TEI cache path must be absolute inside its container.")
    model, revision = option("--model-id"), option("--revision")
    if model != profile["Rag"]["EmbeddingsModel"] or revision != profile["Rag"]["EmbeddingsModelRevision"]:
        raise ValueError("Executed TEI model/revision argv differ from the frozen profile.")
    snapshot = cache / ("models--" + model.replace("/", "--")) / "snapshots" / revision
    if profile.get("containerModelRoot") != str(snapshot):
        raise ValueError("The attested snapshot is not the one selected by TEI argv/cache.")
    mounts = []
    for mount in container.get("Mounts", []):
        destination = PurePosixPath(str(mount.get("Destination", "")))
        if mount.get("Type") == "bind" and (snapshot == destination or destination in snapshot.parents):
            mounts.append((len(destination.parts), mount, destination))
    if not mounts:
        raise ValueError("No actual TEI bind mount contains the selected model snapshot.")
    _, mount, destination = max(mounts, key=lambda value: value[0])
    source = PurePosixPath(str(mount.get("Source", "")))
    host_snapshot = source / snapshot.relative_to(destination)
    return {"hostModelRoot": str(host_snapshot), "containerModelRoot": str(snapshot),
            "containerCacheRoot": str(cache), "modelRevision": revision,
            "modelFileRelativePath": profile.get("modelFileRelativePath"),
            "mountSource": str(source), "mountDestination": str(destination)}
