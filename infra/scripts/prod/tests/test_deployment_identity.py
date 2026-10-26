"""Pure deployment identity tests; no Docker, network, Git or .NET builds."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

PROD = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("deployment_identity", PROD / "deployment_identity.py")
identity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(identity)


class DeploymentIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for relative in ("RAG.sln", "global.json", "Directory.Build.props", ".dockerignore",
                         "backend/SAAIA.Backend/Dockerfile", "backend/SAAIA.Backend/Program.cs",
                         "contracts/SAAIA.Contracts/Model.cs", "infra/docling-sidecar/Dockerfile",
                         "infra/docling-sidecar/entrypoint.sh", "infra/docling-sidecar/saaia_docling/main.py"):
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("synthetic " + relative + "\r\n", encoding="utf-8")
        (self.root / "infra/docker-compose.prod.yml").write_text(
            "image: ${TEI_IMAGE:-registry.invalid/tei@sha256:" + "a" * 64 + "}\n"
            "image: ${QDRANT_IMAGE:-registry.invalid/qdrant@sha256:" + "b" * 64 + "}\n",
            encoding="utf-8")

    def test_source_change_and_build_properties_change_identity(self):
        first = identity.source_revision(self.root, "backend")
        (self.root / "backend/SAAIA.Backend/Program.cs").write_text("changed", encoding="utf-8")
        second = identity.source_revision(self.root, "backend")
        self.assertNotEqual(first, second)
        (self.root / "Directory.Build.props").write_text("changed build", encoding="utf-8")
        self.assertNotEqual(second, identity.source_revision(self.root, "backend"))

    def test_generated_and_private_inputs_do_not_change_identity(self):
        before = identity.source_revision(self.root, "backend")
        for relative in ("backend/SAAIA.Backend/obj/generated.json", "backend/SAAIA.Backend/deployment.config.json",
                         "backend/SAAIA.Backend/appsettings.Local.json", "backend/SAAIA.Backend/.env"):
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("synthetic excluded", encoding="utf-8")
        self.assertEqual(before, identity.source_revision(self.root, "backend"))

    def test_missing_shared_build_input_rejected(self):
        (self.root / "Directory.Build.props").unlink()
        with self.assertRaisesRegex(ValueError, "Missing build input"):
            identity.source_revision(self.root, "backend")

    def test_runtime_digest_derived_from_actual_override(self):
        with patch.dict(os.environ, {"TEI_IMAGE": "registry.invalid/custom@sha256:" + "d" * 64,
                                    "TEI_RUNTIME_REVISION": ""}):
            self.assertEqual("sha256:" + "d" * 64, identity.runtime_revision(self.root, "TEI_IMAGE", "TEI_RUNTIME_REVISION"))

    def test_mismatched_runtime_rejected(self):
        with patch.dict(os.environ, {"TEI_IMAGE": "registry.invalid/custom@sha256:" + "d" * 64,
                                    "TEI_RUNTIME_REVISION": "sha256:" + "e" * 64}):
            with self.assertRaises(ValueError):
                identity.runtime_revision(self.root, "TEI_IMAGE", "TEI_RUNTIME_REVISION")

    def test_mutable_image_needs_explicit_digest_declaration(self):
        with patch.dict(os.environ, {"QDRANT_IMAGE": "registry.invalid/mutable:tag", "QDRANT_RUNTIME_REVISION": ""}):
            with self.assertRaises(ValueError):
                identity.runtime_revision(self.root, "QDRANT_IMAGE", "QDRANT_RUNTIME_REVISION")

    def test_linux_and_powershell_source_identity_are_identical(self):
        executable = shutil.which("pwsh")
        self.assertIsNotNone(executable, "PowerShell required for cross-language identity witness")
        script = (PROD / "_common.ps1").as_posix().replace("'", "''")
        root = self.root.as_posix().replace("'", "''")
        for kind in ("backend", "docling"):
            result = subprocess.run([executable, "-NoProfile", "-Command",
                                     f". '{script}'; Get-DeploymentSourceRevision -RepoRoot '{root}' -Kind {kind}"],
                                    capture_output=True, text=True, check=True)
            self.assertEqual(identity.source_revision(self.root, kind), result.stdout.strip())


if __name__ == "__main__":
    unittest.main()
