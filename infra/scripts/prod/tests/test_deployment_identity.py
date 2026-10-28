"""Pure deployment identity tests; no Docker, network, Git or .NET builds."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import tarfile
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
                         "infra/docling-sidecar/entrypoint.sh", "infra/docling-sidecar/requirements-parser.txt",
                         "infra/docling-sidecar/saaia_docling/main.py"):
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
        script = (PROD / "DeploymentIdentity.ps1").as_posix().replace("'", "''")
        root = self.root.as_posix().replace("'", "''")
        for kind in ("backend", "docling"):
            result = subprocess.run([executable, "-NoProfile", "-Command",
                                     f". '{script}'; Get-DeploymentSourceRevision -RepoRoot '{root}' -Kind {kind}"],
                                    capture_output=True, text=True, check=True)
            self.assertEqual(identity.source_revision(self.root, kind), result.stdout.strip())


    def test_parser_dependency_changes_only_docling_source_identity(self):
        dependency = self.root / "infra/docling-sidecar/requirements-parser.txt"
        dependency.write_bytes(b"pydantic-core==2.46.4\n")
        docling_before = identity.source_revision(self.root, "docling")
        backend_before = identity.source_revision(self.root, "backend")
        dependency.write_bytes(b"pydantic-core==2.46.5\n")
        self.assertNotEqual(docling_before, identity.source_revision(self.root, "docling"))
        self.assertEqual(backend_before, identity.source_revision(self.root, "backend"))

    def test_parser_dependency_is_a_required_build_input(self):
        (self.root / "infra/docling-sidecar/requirements-parser.txt").unlink()
        with self.assertRaisesRegex(ValueError, "Missing build input: infra/docling-sidecar/requirements-parser.txt"):
            identity.source_revision(self.root, "docling")

    def test_parser_pin_mutation_preserves_cross_language_identity_agreement(self):
        executable = shutil.which("pwsh")
        self.assertIsNotNone(executable, "PowerShell required for cross-language identity witness")
        script = (PROD / "DeploymentIdentity.ps1").as_posix().replace("'", "''")
        root = self.root.as_posix().replace("'", "''")
        for pin in (b"pydantic-core==2.46.4\n", b"pydantic-core==2.46.5\n"):
            (self.root / "infra/docling-sidecar/requirements-parser.txt").write_bytes(pin)
            result = subprocess.run([executable, "-NoProfile", "-Command",
                                     f". '{script}'; Get-DeploymentSourceRevision -RepoRoot '{root}' -Kind docling"],
                                    capture_output=True, text=True, check=True)
            self.assertEqual(identity.source_revision(self.root, "docling"), result.stdout.strip())

    def test_real_source_archive_carries_exact_parser_pin_and_reproduces_identities(self):
        executable = shutil.which("pwsh")
        self.assertIsNotNone(executable, "PowerShell required for source archive witness")
        self.assertIsNotNone(shutil.which("tar"), "tar required for source archive witness")
        dependency_name = "infra/docling-sidecar/requirements-parser.txt"
        dependency = self.root / dependency_name
        dependency.write_bytes(b"pydantic-core==2.46.4\n")
        expected = {kind: identity.source_revision(self.root, kind) for kind in ("backend", "docling")}
        # This synthetic excluded input must not cross the archive boundary.
        excluded = self.root / "backend/SAAIA.Backend/keys/synthetic.pem"
        excluded.parent.mkdir(parents=True)
        excluded.write_text("SYNTHETIC_EXCLUDED_NOT_A_SECRET", encoding="utf-8")
        archive = self.root / "fixture-source.tar"
        script = (PROD / "DeploymentIdentity.ps1").as_posix().replace("'", "''")
        quoted_root = self.root.as_posix().replace("'", "''")
        quoted_archive = archive.as_posix().replace("'", "''")
        subprocess.run([executable, "-NoProfile", "-Command",
                        f"$ErrorActionPreference='Stop'; . '{script}'; New-BackendSourceArchive -RepoRoot '{quoted_root}' -ArchivePath '{quoted_archive}'"],
                       capture_output=True, text=True, check=True)
        extracted = self.root / "archive-readback"
        extracted.mkdir()
        with tarfile.open(archive, "r:") as package:
            names = package.getnames()
            self.assertIn(dependency_name, names)
            self.assertEqual(package.extractfile(dependency_name).read(), dependency.read_bytes())
            self.assertNotIn("backend/SAAIA.Backend/keys/synthetic.pem", names)
            self.assertTrue(all(member.isfile() for member in package.getmembers()))
            package.extractall(extracted, filter="data")
        for kind, revision in expected.items():
            self.assertEqual(identity.source_revision(extracted, kind), revision)
        self.assertEqual(list(self.root.glob("fixture-source.tar.inputs-*")), [])


if __name__ == "__main__":
    unittest.main()
