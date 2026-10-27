"""Pure existing ingestion wire/LLM/Production guards, reused without starting a runner.

Copied verbatim from validation-ingestion/invoke-linux-ingestion.py; no historical
runner, OCR evaluator or legacy corpus is imported into the four-book campaign.
"""
from __future__ import annotations
import uuid


def synthetic_admin_headers(key: str) -> dict[str, str]:
    """One isolated bootstrap admin key, never a regular user's credentials."""
    return {"X-Api-Key": key, "X-Admin-Key": key}


def validate_full_document_timeouts(policy: dict, config: dict, docling: dict) -> dict:
    seconds = policy["backendFullDocumentTimeoutSeconds"]
    proxy = 900 if seconds == 900 else 3900
    env_rows = docling["Config"]["Env"]
    names = ("SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS", "DOCLING_SERVE_MAX_SYNC_WAIT")
    env = dict(item.split("=",1) for item in env_rows if "=" in item)
    if (type(seconds) is not int or seconds not in (900,3600)
            or policy["doclingUpstreamTimeoutSeconds"] != proxy or policy["doclingMaximumSyncWaitSeconds"] != proxy
            or config["DocumentIntelligence"]["TimeoutSeconds"] != seconds
            or any(sum(item.startswith(name+"=") for item in env_rows) != 1 or env.get(name) != str(proxy) for name in names)):
        raise ValueError("observed_full_document_timeout_contract_changed")
    return {"signedBackendSeconds":seconds,"observedProxyUpstreamSeconds":proxy,"observedMaxSyncWaitEnvironmentSeconds":proxy,
            "maxSyncWaitPackageSettingObservedByThisFunction":False}


def parse_job_listing(response: dict) -> list[dict]:
    """Dapper dictionaries preserve PascalCase, unlike typed camelCase DTOs.

    Accept the two explicit wire forms; never silently coalesce conflicting
    aliases or treat missing status as a running job until a timeout.
    """
    if not isinstance(response, dict):
        raise ValueError("Ingestion job listing must be an explicit JSON object.")
    def field(value: dict, camel: str, pascal: str):
        present = [name for name in (camel, pascal) if name in value]
        if len(present) != 1:
            raise ValueError("Ingestion job wire has a missing or ambiguous required field: " + camel)
        return value[present[0]]
    rows = field(response, "items", "Items")
    if not isinstance(rows, list) or len(rows) > 200:
        raise ValueError("Ingestion job listing must be a bounded explicit array.")
    parsed = []
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("An ingestion job wire item is not an object.")
        item = {name: field(row, name, name[0].upper() + name[1:])
                for name in ("jobId", "action", "docPath", "status", "attempts")}
        if (not isinstance(item["jobId"], str) or not isinstance(item["docPath"], str)
                or not item["docPath"] or len(item["docPath"]) > 4096
                or item["action"] not in {"upsert", "delete"}
                or item["status"] not in {"queued", "running", "paused", "done", "failed", "canceled", "cancelled"}
                or type(item["attempts"]) is not int or item["attempts"] < 0):
            raise ValueError("Ingestion job wire identity, action, status or attempts is invalid.")
        item["jobId"] = str(uuid.UUID(item["jobId"]))
        parsed.append(item)
    if len({item["jobId"] for item in parsed}) != len(parsed):
        raise ValueError("An ingestion job is duplicated in the wire listing.")
    return parsed


def validate_disabled_llm(config: dict, backend: dict) -> None:
    # The signed deployment document wins over environment in Production.
    env = dict(item.split("=", 1) for item in backend.get("Config", {}).get("Env", []) if "=" in item)
    advanced = config.get("AdvancedAnalysis", {})
    if (advanced.get("WorkerEnabled") is not False or advanced.get("Provider") != "disabled"
            or config.get("License", {}).get("AdvancedAnalysisEnabled") is not False):
        raise ValueError("The signed qualification config must explicitly disable advanced/provider capabilities.")
    for name in ("AllowExternalProviderContent", "AllowExternalProviderMetadata"):
        if advanced.get(name) is not False:
            raise ValueError("External corpus transmission must be explicitly disabled.")
    cap_b = config.get("RuntimeGovernance", {}).get("CapabilityBWorkerEnabled")
    if cap_b is None:
        cap_b = env.get("RuntimeGovernance__CapabilityBWorkerEnabled", "").lower() == "true" if "RuntimeGovernance__CapabilityBWorkerEnabled" in env else None
    if cap_b is not False or env.get("BACKOFFICE_LLM_ENABLED", "false").lower() != "false":
        raise ValueError("Automatic/backoffice LLM workers must be explicitly disabled.")
    ingestion = config.get("Ingestion", {})
    for name in ("WatcherEnabled", "ScannerEnabled"):
        value = ingestion.get(name)
        if value is None and "Ingestion__" + name in env:
            value = env["Ingestion__" + name].lower() == "true"
        if value is not False:
            raise ValueError("Only API-triggered ingestion may run in this isolated qualification.")


def qualification_identity(owner: dict, backend: dict, readiness: dict) -> dict:
    env = dict(item.split("=", 1) for item in backend.get("Config", {}).get("Env", []) if "=" in item)
    declared_environments = {env[name] for name in ("DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT") if env.get(name)}
    if len(declared_environments) > 1:
        raise ValueError("Conflicting .NET environment declarations cannot qualify a deployment.")
    environment = next(iter(declared_environments), "Production")
    details = readiness.get("details", {})
    signature = details.get("config_signature_verified") is True
    mode = owner.get("qualificationMode", "production")
    if mode == "production" and (environment != "Production" or not signature):
        raise ValueError("Production qualification requires the real Production environment and verified signature.")
    if mode == "development_diagnostic" and environment != "Development":
        raise ValueError("The explicit diagnostic mode is reserved for the observed Development environment.")
    return {"qualificationMode": mode, "environmentObservedFromContainer": environment,
            "configurationSignatureMode": details.get("config_signature_mode"),
            "configurationSignatureVerified": signature,
            "deploymentQualificationEligible": mode == "production" and environment == "Production" and signature}
