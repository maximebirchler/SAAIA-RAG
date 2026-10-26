"""Owned Linux Docker observations and cleanup. Importing executes nothing.

This module never creates a container, builds, pulls, installs or uses SSH.
The operator supplies an independently prepared, isolated runtime manifest.
"""
from __future__ import annotations

import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import urllib.parse
import uuid

QUALIFICATION_PARENT = PurePosixPath("/home/maxime/saaia-ingestion-qualification")
ROLES = {"postgres", "qdrant", "tei", "docling", "backend"}
RUN_LABEL = "com.saaia.qualification.run-id"


def mounted_source(container: dict, container_path: str) -> str:
    """Map a file through the most specific actual bind, including overlays."""
    path = PurePosixPath(container_path)
    candidates = []
    for mount in container.get("Mounts", []):
        destination = PurePosixPath(str(mount.get("Destination", "")))
        if mount.get("Type") == "bind" and (path == destination or destination in path.parents):
            candidates.append((len(destination.parts), mount, destination))
    if not candidates:
        raise ValueError("No actual bind contains the selected container artifact.")
    _, mount, destination = max(candidates, key=lambda value: value[0])
    return str(PurePosixPath(mount["Source"]) / path.relative_to(destination))


def loopback_origin(value: str) -> str:
    parsed = urllib.parse.urlparse(value)
    if (parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or not parsed.port
            or not 1024 <= parsed.port <= 65535 or parsed.port == 5432
            or parsed.username is not None or parsed.password is not None
            or parsed.query or parsed.fragment or parsed.path not in {"", "/"}):
        raise ValueError("A literal, uncredentialed, high-port loopback origin is required.")
    return value.rstrip("/")


def validate_owner(owner: dict) -> dict:
    if not isinstance(owner, dict) or owner.get("schemaVersion") != "saaia-linux-ingestion-runtime.v1":
        raise ValueError("An explicit Linux qualification ownership manifest is required.")
    run = uuid.UUID(str(owner.get("runId", "")))
    root = PurePosixPath(str(owner.get("root", "")))
    if (not root.is_absolute() or ".." in root.parts or root.parent != QUALIFICATION_PARENT
            or root.name not in {str(run), run.hex}
            or owner.get("project") != "saaiaqual_" + run.hex[:12]):
        raise ValueError("The run must own its exact dedicated home-directory scope and project.")
    expected = {"documentsRoot": root / "documents", "configurationPath": root / "deploy/deployment.config.json"}
    for key, value in expected.items():
        if owner.get(key) != str(value):
            raise ValueError("Runtime paths differ from the exact owned layout.")
    endpoints = owner.get("endpoints", {})
    if set(endpoints) != {"backend", "tei", "qdrant", "docling"}:
        raise ValueError("Declare exactly the four loopback qualification endpoints.")
    origins = [loopback_origin(value) for value in endpoints.values()]
    if len(set(origins)) != len(origins):
        raise ValueError("Qualification endpoints must have distinct host ports.")
    records = owner.get("containers")
    if (not isinstance(records, list) or len(records) != len(ROLES)
            or {r.get("role") for r in records} != ROLES):
        raise ValueError("Exactly the five declared qualification containers are required.")
    for record in records:
        if (not re.fullmatch(r"[a-f0-9]{64}", str(record.get("id", "")))
                or not re.fullmatch(r"sha256:[a-f0-9]{64}", str(record.get("imageId", "")))
                or not re.fullmatch(r"[a-z][a-z0-9-]{0,63}", str(record.get("service", "")))):
            raise ValueError("Full container IDs, image IDs and exact Compose services are required.")
    if len({r["id"] for r in records}) != len(records):
        raise ValueError("Container identities cannot be reused across roles.")
    for key in ("database", "databaseUser"):
        if not re.fullmatch(r"saaia_qual_[a-z0-9_]{1,48}", str(owner.get(key, ""))):
            raise ValueError("Use explicitly synthetic qualification database identities.")
    collection = owner.get("qdrantCollection", "")
    if collection != "ingestion_" + run.hex:
        raise ValueError("The collection must be unique to the complete run identity.")
    if owner.get("networkPolicy", "internal") not in {"internal", "dedicated_bridge"}:
        raise ValueError("Use an explicit supported qualification network policy.")
    if owner.get("qualificationMode", "production") not in {"production", "development_diagnostic"}:
        raise ValueError("Production qualification and Development diagnostics must remain explicit.")
    return owner


def assert_owned_path(path: Path, root: Path, *, allow_root: bool = False) -> Path:
    # Lexical checks precede resolve so a symlink cannot silently expand scope.
    if not path.is_absolute() or ".." in path.parts or (path == root and not allow_root):
        raise ValueError("An absolute child of the exact owned root is required.")
    if path != root and root not in path.parents:
        raise ValueError("The requested path leaves the qualification root.")
    for segment in [path, *path.parents]:
        if segment.is_symlink():
            raise ValueError("Qualification paths cannot traverse symlinks.")
        if segment == root.parent:
            break
    if path.resolve(strict=False) != path:
        raise ValueError("Qualification path resolution changed its scope.")
    return path


def validate_container(owner: dict, record: dict, observed: dict) -> dict:
    validate_owner(owner)
    labels = observed.get("Config", {}).get("Labels") or {}
    if (observed.get("Id") != record["id"] or observed.get("Image") != record["imageId"]
            or labels.get("com.docker.compose.project") != owner["project"]
            or labels.get("com.docker.compose.service") != record["service"]
            or labels.get(RUN_LABEL) not in {owner["runId"], uuid.UUID(owner["runId"]).hex}):
        raise ValueError("Container ownership/image/project/service labels do not match.")
    host = observed.get("HostConfig", {})
    if int(host.get("Memory", 0)) <= 0 or int(host.get("NanoCpus", 0)) <= 0:
        raise ValueError("Every qualification container needs explicit memory and CPU bounds.")
    if host.get("Privileged") or host.get("PidMode") == "host" or host.get("IpcMode") == "host":
        raise ValueError("Qualification cannot acquire host process or privileged access.")
    if host.get("Devices") or host.get("DeviceRequests"):
        raise ValueError("This CPU qualification cannot request host devices or a GPU.")
    mounts = observed.get("Mounts", [])
    for mount in mounts:
        source = PurePosixPath(str(mount.get("Source", "")))
        if (mount.get("Type") != "bind" or not source.is_absolute() or ".." in source.parts
                or PurePosixPath(owner["root"]) not in source.parents):
            raise ValueError("Only bind mounts under the exact qualification root are permitted.")
    role = record["role"]
    ports = host.get("PortBindings") or observed.get("NetworkSettings", {}).get("Ports") or {}
    host_ports = []
    for bindings in ports.values():
        for binding in bindings or []:
            if binding.get("HostIp") != "127.0.0.1" or not str(binding.get("HostPort", "")).isdigit():
                raise ValueError("Published qualification ports must bind only literal IPv4 loopback.")
            port = int(binding["HostPort"])
            if not 1024 <= port <= 65535 or port == 5432:
                raise ValueError("A privileged or usual PostgreSQL port is outside qualification scope.")
            host_ports.append(port)
    if role != "postgres":
        required = urllib.parse.urlparse(owner["endpoints"][role]).port
        if host_ports != [required]:
            raise ValueError("Exactly the declared role endpoint must be published.")
    elif host_ports:
        raise ValueError("Qualification PostgreSQL is accessed by owned docker exec, never a host port.")
    networks = observed.get("NetworkSettings", {}).get("Networks") or {}
    if not networks or any(not name.startswith(owner["project"] + "_") for name in networks):
        raise ValueError("No shared or external Docker network may be attached.")
    if role == "backend":
        entrypoint = observed.get("Config", {}).get("Entrypoint")
        if entrypoint not in (["dotnet", "SAAIA.Backend.dll"], ["dotnet", "/app/SAAIA.Backend.dll"]) or observed.get("Config", {}).get("Cmd"):
            raise ValueError("Only the published backend entrypoint without CLI configuration overrides is permitted.")
        actual_mounts = {m.get("Destination"): m.get("Source") for m in mounts}
        if (actual_mounts.get("/app/documents") != owner["documentsRoot"]
                or actual_mounts.get("/app/deployment.config.json") != owner["configurationPath"]):
            raise ValueError("The backend must mount the exact owned documents and signed configuration.")
        forbidden = ("OPENAI_API_KEY", "SAAIA_ADVANCED_LLM_API_KEY", "AZURE_OPENAI_API_KEY", "RUNPOD_API_KEY")
        env = dict(item.split("=", 1) for item in observed.get("Config", {}).get("Env", []) if "=" in item)
        if any(env.get(key) for key in forbidden):
            raise ValueError("No paid-provider credential may be present in the qualification backend.")
    return {"role": role, "id": record["id"], "imageId": record["imageId"],
            "memoryLimitBytes": host["Memory"], "nanoCpus": host["NanoCpus"],
            "loopbackPorts": host_ports, "running": observed.get("State", {}).get("Running") is True}


class OwnedDocker:
    def __init__(self, owner: dict, execute: bool = False):
        self.owner = validate_owner(owner)
        self.execute = execute

    def command(self, arguments: list[str], *, stdin: str | None = None, timeout: int = 30) -> str:
        if not self.execute:
            raise PermissionError("Docker execution requires explicit --execute.")
        result = subprocess.run(["docker", "--host", "unix:///var/run/docker.sock", *arguments], input=stdin, encoding="utf-8", errors="strict",
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout, check=False)
        if result.returncode:
            # Docker stderr can include private config. Never expose it in errors.
            raise RuntimeError("Owned Docker command failed with exit code " + str(result.returncode))
        return result.stdout

    def inspect_all(self, *, require_running: bool = True) -> tuple[list[dict], list[dict]]:
        records = self.owner["containers"]
        observed = json.loads(self.command(["inspect", *[r["id"] for r in records]]))
        if not isinstance(observed, list) or len(observed) != len(records):
            raise ValueError("Docker inspect did not return every exact owned container.")
        by_id = {item.get("Id"): item for item in observed}
        safe = [validate_container(self.owner, record, by_id.get(record["id"], {})) for record in records]
        for item in observed:
            for mount in item.get("Mounts", []):
                assert_owned_path(Path(mount["Source"]), Path(self.owner["root"]))
        if require_running and any(not item["running"] for item in safe):
            raise ValueError("Every owned runtime container must be running before an observation.")
        networks = sorted({name for item in observed for name in item.get("NetworkSettings", {}).get("Networks", {})})
        network_info = json.loads(self.command(["network", "inspect", *networks]))
        if (len(network_info) != len(networks) or {n.get("Name") for n in network_info} != set(networks)
                or any((n.get("Labels") or {}).get("com.docker.compose.project") != self.owner["project"]
                       or (self.owner.get("networkPolicy", "internal") == "internal" and n.get("Internal") is not True)
                       for n in network_info)):
            raise ValueError("Observed Docker networks differ from the explicit owned project/network policy.")
        for item in safe:
            item["externalEgressBlockedByDockerInternalNetwork"] = all(n.get("Internal") is True for n in network_info)
        return safe, observed

    def sql(self, query: str) -> str:
        self.inspect_all()
        record = next(item for item in self.owner["containers"] if item["role"] == "postgres")
        # Queries are static runner-owned SELECTs, never user SQL. A server-side
        # read-only transaction remains a second barrier if a future query drifts.
        sql = "BEGIN READ ONLY;\nSET LOCAL client_encoding='UTF8';\n" + query + ";\nROLLBACK;\n"
        return self.command(["exec", "-i", record["id"], "psql", "-X", "-A", "-t", "-q", "-w",
                             "-v", "ON_ERROR_STOP=1", "--username", self.owner["databaseUser"],
                             "--dbname", self.owner["database"]], stdin=sql, timeout=60).strip()

    def stop(self) -> dict:
        # Validate the complete set before stopping any resource. No compose down,
        # volume removal, network removal, image pruning or kill-tree is used.
        self.inspect_all(require_running=False)
        order = {"backend": 0, "docling": 1, "tei": 2, "qdrant": 3, "postgres": 4}
        for record in sorted(self.owner["containers"], key=lambda r: order[r["role"]]):
            one = json.loads(self.command(["inspect", record["id"]]))[0]
            validate_container(self.owner, record, one)
            if one.get("State", {}).get("Running"):
                self.command(["stop", "--time", "45", record["id"]], timeout=65)
        safe, _ = self.inspect_all(require_running=False)
        ports = [port for item in safe for port in item["loopbackPorts"]]
        released = all(not self.command(["ps", "--quiet", "--filter", "publish=" + str(port)]).strip() for port in ports)
        return {"ownedContainersStopped": all(not x["running"] for x in safe), "dockerPublishedPortBindingsAbsent": released,
                "containerCount": len(safe), "volumesDeleted": False, "imagesDeleted": False}
