from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import uuid
from pathlib import Path
from typing import Any

from .evaluator import evaluate_case
from .fixtures import generate_fixtures


def _post_local(base_url: str, pdf_path: Path, force_ocr: bool) -> dict[str, Any]:
    boundary = f"----saaia-{uuid.uuid4().hex}"
    fields = {
        "to_formats": "json",
        "do_ocr": "true",
        "force_ocr": "true" if force_ocr else "false",
        "ocr_preset": "auto",
        "pdf_backend": "docling_parse",
        "table_mode": "accurate",
        "table_cell_matching": "true",
        "do_table_structure": "true",
        "include_images": "false",
        "include_page_images": "false",
    }
    chunks: list[bytes] = []
    for name, value in fields.items():
        chunks.extend(
            [
                f"--{boundary}\r\n".encode(),
                f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode(),
                value.encode(),
                b"\r\n",
            ]
        )
    chunks.extend(
        [
            f"--{boundary}\r\n".encode(),
            (
                'Content-Disposition: form-data; name="files"; '
                f'filename="{pdf_path.name}"\r\n'
            ).encode(),
            b"Content-Type: application/pdf\r\n\r\n",
            pdf_path.read_bytes(),
            b"\r\n",
            f"--{boundary}--\r\n".encode(),
        ]
    )
    request = urllib.request.Request(
        base_url.rstrip("/") + "/v1/convert/file",
        data=b"".join(chunks),
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=1200) as response:
        return json.loads(response.read().decode("utf-8"))


def _run_checked(command: list[str], *, input_text: str | None = None) -> str:
    result = subprocess.run(
        command,
        input=input_text,
        text=True,
        encoding="utf-8",
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"Command failed ({result.returncode}): {' '.join(command)}\n"
            f"{result.stderr.strip()}"
        )
    return result.stdout


def _post_remote(
    ssh_target: str,
    request_container: str,
    pdf_path: Path,
    force_ocr: bool,
) -> dict[str, Any]:
    token = uuid.uuid4().hex
    remote_host_path = f"/tmp/saaia-layout-gold-{token}.pdf"
    container_path = f"/tmp/saaia-layout-gold-{token}.pdf"
    _run_checked(["scp", "-q", str(pdf_path), f"{ssh_target}:{remote_host_path}"])
    try:
        _run_checked(
            [
                "ssh",
                ssh_target,
                f"docker cp {remote_host_path} {request_container}:{container_path}",
            ]
        )
        force_value = "true" if force_ocr else "false"
        remote_command = (
            f"docker exec {request_container} curl -fsS "
            "-X POST "
            "-F to_formats=json "
            "-F do_ocr=true "
            f"-F force_ocr={force_value} "
            "-F ocr_preset=auto "
            "-F pdf_backend=docling_parse "
            "-F table_mode=accurate "
            "-F table_cell_matching=true "
            "-F do_table_structure=true "
            "-F include_images=false "
            "-F include_page_images=false "
            f"-F files=@{container_path} "
            "http://127.0.0.1:5001/v1/convert/file"
        )
        return json.loads(_run_checked(["ssh", ssh_target, remote_command]))
    finally:
        subprocess.run(
            [
                "ssh",
                ssh_target,
                f"docker exec {request_container} rm -f {container_path}",
            ],
            capture_output=True,
            check=False,
        )
        subprocess.run(
            [
                "ssh",
                ssh_target,
                f"rm -f {remote_host_path}",
            ],
            capture_output=True,
            check=False,
        )


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate and evaluate legal synthetic PDF layout fixtures."
    )
    parser.add_argument(
        "--manifest",
        default="config/ingestion-layout-gold.v1.json",
    )
    parser.add_argument("--base-url", default="")
    parser.add_argument("--ssh-target", default="")
    parser.add_argument("--request-container", default="infra-docling-1")
    parser.add_argument("--output", default="")
    parser.add_argument("--keep-workdir", action="store_true")
    args = parser.parse_args()
    if bool(args.base_url) == bool(args.ssh_target):
        parser.error("Specify exactly one of --base-url or --ssh-target.")

    manifest_path = Path(args.manifest).resolve()
    workdir = Path(tempfile.mkdtemp(prefix="saaia-ingestion-gold-"))
    output_path = (
        Path(args.output).resolve()
        if args.output
        else workdir / "report.json"
    )
    try:
        generated = generate_fixtures(manifest_path, workdir)
        results: list[dict[str, Any]] = []
        for generated_case in generated:
            case = generated_case["case"]
            pdf_path = generated_case["path"]
            started = time.perf_counter()
            if args.base_url:
                response = _post_local(
                    args.base_url,
                    pdf_path,
                    bool(case.get("forceOcr", False)),
                )
            else:
                response = _post_remote(
                    args.ssh_target,
                    args.request_container,
                    pdf_path,
                    bool(case.get("forceOcr", False)),
                )
            duration = time.perf_counter() - started
            evaluation = evaluate_case(response, case)
            response_path = output_path.parent / f"{case['caseId']}.response.json"
            response_path.parent.mkdir(parents=True, exist_ok=True)
            response_path.write_text(
                json.dumps(response, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            results.append(
                {
                    "caseId": case["caseId"],
                    "sourceSha256": generated_case["sourceSha256"],
                    "durationSeconds": round(duration, 3),
                    **evaluation,
                }
            )
        report = {
            "schemaVersion": "ingestion_layout_gold_report_v1",
            "ok": all(result["ok"] for result in results),
            "caseCount": len(results),
            "passedCaseCount": sum(1 for result in results if result["ok"]),
            "results": results,
        }
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(
            json.dumps(report, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0 if report["ok"] else 1
    finally:
        if args.keep_workdir:
            print(f"Fixture workdir kept at {workdir}", file=sys.stderr)
        else:
            shutil.rmtree(workdir, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
