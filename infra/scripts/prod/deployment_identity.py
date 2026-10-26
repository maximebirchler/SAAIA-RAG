"""Offline deployment identity used by the Linux installer (standard library only).

Same byte inventory contract as DeploymentIdentity.ps1. This declares exact build
inputs, not a claim that a Docker image or running DLL has been qualified.
"""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import re


def source_revision(root: Path, kind: str) -> str:
    required = (["RAG.sln", "global.json", "Directory.Build.props", ".dockerignore",
                 "backend/SAAIA.Backend/Dockerfile"] if kind == "backend" else
                ["infra/docling-sidecar/Dockerfile", "infra/docling-sidecar/entrypoint.sh"])
    trees = (["backend/SAAIA.Backend", "contracts/SAAIA.Contracts"] if kind == "backend"
             else ["infra/docling-sidecar/saaia_docling"])
    for relative in required:
        if not (root / relative).is_file():
            raise ValueError(f"Missing build input: {relative}")
    paths = set(required)
    for tree in trees:
        for path in (root / tree).rglob("*"):
            if not path.is_file():
                continue
            relative = path.relative_to(root).as_posix()
            if re.search(r"(^|/)(bin|obj|bin-codex[^/]*|obj-codex[^/]*|\.vs|\.idea|keys|__pycache__)(/|$)", relative, re.I):
                continue
            if re.search(r"^(\.env($|\.)|appsettings\.Local\.json$|appsettings\..*\.local\.json$|deployment\.config\.(json|sig)$|private.*\.b64$)", path.name, re.I):
                continue
            if re.search(r"\.(user|suo|log|tmp|bak|zip|7z|tgz|pdf|gguf|bin|safetensors|pem|key|crt|cer|pfx|p8|p12|pyc)$|\.tar\.gz$", path.name, re.I):
                continue
            paths.add(relative)
    lines = []
    for relative in sorted(paths):
        data = (root / relative).read_bytes()
        lines.append(f"{relative}|{len(data)}|{hashlib.sha256(data).hexdigest()}")
    return "source-sha256:" + hashlib.sha256("\n".join(lines).encode("utf-8")).hexdigest()


def runtime_revision(root: Path, image_variable: str, revision_variable: str) -> str:
    compose = (root / "infra/docker-compose.prod.yml").read_text(encoding="utf-8-sig")
    default = re.search(r"\$\{" + re.escape(image_variable) + r":-([^}]+)\}", compose)
    if not default:
        raise ValueError(f"Missing Compose default for {image_variable}")
    image = os.environ.get(image_variable, "").strip() or default[1]
    revision = os.environ.get(revision_variable, "").strip().lower()
    digest = re.search(r"@(sha256:[0-9a-fA-F]{64})$", image)
    if digest:
        if revision and revision != digest[1].lower():
            raise ValueError(f"{revision_variable} does not match the digest of {image_variable}")
        return digest[1].lower()
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", revision):
        raise ValueError(f"{revision_variable} must identify the observed image digest for mutable {image_variable}")
    return revision


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", required=True, type=Path)
    sub = parser.add_subparsers(dest="command", required=True)
    source = sub.add_parser("source")
    source.add_argument("kind", choices=("backend", "docling"))
    runtime = sub.add_parser("runtime")
    runtime.add_argument("image_variable", choices=("TEI_IMAGE", "QDRANT_IMAGE"))
    runtime.add_argument("revision_variable", choices=("TEI_RUNTIME_REVISION", "QDRANT_RUNTIME_REVISION"))
    args = parser.parse_args()
    try:
        print(source_revision(args.repo_root, args.kind) if args.command == "source" else
              runtime_revision(args.repo_root, args.image_variable, args.revision_variable))
    except (OSError, ValueError) as error:
        parser.exit(2, f"Deployment identity rejected: {error}\n")
