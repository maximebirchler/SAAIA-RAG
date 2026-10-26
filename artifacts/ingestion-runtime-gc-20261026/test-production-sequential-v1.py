"""Pure operator protocol guards: no Docker, HTTP, SSH or runner execution."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

SPEC = importlib.util.spec_from_file_location('sequential', Path(__file__).with_name('run-production-sequential-v1.py'))
M = importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(M)


def registry():
    rows = []
    for index, role in enumerate(M.ORDER):
        identity = uuid.UUID(int=index + 100)
        rows.append({'role': role, 'runId': str(identity), 'root': str(M.CAT.parent / str(identity)),
                     'profile': role if role in M.AUTO else 'manual', 'configurationSha256': 'a' * 64})
    return {'schemaVersion': 'saaia-isolated-production-config-signing-inputs.v1', 'prepared': rows}


class OperatorGuards(unittest.TestCase):
    def test_default_selection_never_touches_running_scanner_or_completed_manuals(self):
        self.assertEqual(('watcher', 'defaults'), M.DEFAULT_SELECTION)
        rows = M.registry_rows(registry(), M.DEFAULT_SELECTION)
        self.assertEqual(['watcher', 'defaults'], [r['role'] for r in rows])

    def test_selection_is_in_declared_order_not_caller_order(self):
        rows = M.registry_rows(registry(), ['defaults', 'watcher'])
        self.assertEqual(['watcher', 'defaults'], [r['role'] for r in rows])

    def test_registry_refuses_duplicate_namespace_foreign_root_or_profile(self):
        for key, value in (('runId', registry()['prepared'][1]['runId']), ('root', '/opt/saaia'),
                           ('profile', 'development')):
            obj = registry(); obj['prepared'][0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                M.registry_rows(obj, M.DEFAULT_SELECTION)

    def test_output_names_match_explicit_phase_proofs(self):
        self.assertEqual('automatic-watcher-production-v1', M.output_name('watcher'))
        self.assertEqual('automatic-defaults-production-v1', M.output_name('defaults'))
        self.assertEqual('gold-production-final-v1', M.output_name('gold-production'))

    def prepare_pdf(self, root):
        content = b'synthetic known PDF bytes for pure copy protocol'
        source = root / 'corpus/gold/sources/fr-native-critical-v2.pdf'
        source.parent.mkdir(parents=True); source.write_bytes(content)
        (root / 'documents').mkdir()
        manifest = {'cases': [{'caseId': 'fr-native-critical-v2', 'sourceSha256': hashlib.sha256(content).hexdigest()}]}
        def child(parent, relative):
            path = parent / relative
            if parent not in path.parents: raise ValueError('outside')
            return path
        def new_file(path, data):
            with path.open('xb') as stream: stream.write(data)
        return SimpleNamespace(child=child, new_file=new_file), manifest, content

    def test_defaults_preload_is_exact_and_precedes_backend(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); boot, manifest, content = self.prepare_pdf(root)
            result = M.defaults_preload(root, {'containers': []}, manifest, boot)
            self.assertTrue(result['copiedBeforeBackendCreation'])
            self.assertEqual(content, (root / 'documents/auto-a.pdf').read_bytes())
            M.defaults_preload(root, {'containers': []}, manifest, boot)

    def test_defaults_never_overwrites_unknown_file_or_creates_after_backend(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); boot, manifest, _ = self.prepare_pdf(root)
            with self.assertRaisesRegex(ValueError, 'preexist_backend'):
                M.defaults_preload(root, {'containers': [{'role': 'backend'}]}, manifest, boot)
            target = root / 'documents/auto-a.pdf'; target.write_bytes(b'unknown')
            with self.assertRaises(ValueError): M.defaults_preload(root, {'containers': []}, manifest, boot)
            self.assertEqual(b'unknown', target.read_bytes())

    def test_defaults_requires_original_hash_before_creating_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); boot, manifest, _ = self.prepare_pdf(root)
            manifest['cases'][0]['sourceSha256'] = '0' * 64
            with self.assertRaisesRegex(ValueError, 'source_hash_mismatch'):
                M.defaults_preload(root, {'containers': []}, manifest, boot)
            self.assertFalse((root / 'documents/auto-a.pdf').exists())

    def test_readiness_failure_does_not_become_production_eligibility(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); (root / 'private').mkdir(); (root / 'private/api-key.txt').write_text('synthetic-local')
            owner = {'root': str(root), 'containers': [{'role': 'backend', 'id': 'a' * 64}],
                     'endpoints': {'backend': 'http://127.0.0.1:19001'}}
            transport = SimpleNamespace(inspect_all=lambda: ([], [{'Id': 'a' * 64}]))
            corpus = SimpleNamespace(synthetic_admin_headers=lambda key: {},
                qualification_identity=lambda *_: {'deploymentQualificationEligible': False})
            class Reply:
                status = 200
                def __enter__(self): return self
                def __exit__(self, *unused): pass
                def read(self, maximum): return json.dumps({'ok': True, 'details': {
                    'canonical_provenance_ready': True, 'canonical_artifacts_enabled': True, 'tei_dim': 768,
                    'config_signature_verified': False}}).encode()
            opener = SimpleNamespace(open=lambda *a, **k: Reply())
            with patch.object(M.urllib.request, 'build_opener', return_value=opener):
                with self.assertRaisesRegex(ValueError, 'signature_not_verified'):
                    M.wait_ready(owner, transport, corpus, 30)


if __name__ == '__main__': unittest.main()
