"""Pure bootstrap guards with synthetic configs/secrets and an in-memory Docker.

No real Docker, .NET, SSH, HTTP, keyring or private signing key is accessed.
"""
import base64
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

SPEC = importlib.util.spec_from_file_location('bootstrap', Path(__file__).with_name('bootstrap-owned-linux-v4.py'))
BOOT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BOOT)


def owner(root, profile='scanner'):
    identity = uuid.UUID(root.name)
    return {'root': str(root), 'runId': root.name, 'project': 'saaiaqual_' + identity.hex[:12],
            'admissionProfile': profile, 'containers': [], 'network': 'saaiaqual_' + identity.hex[:12] + '_internal',
            'networkPolicy': 'internal', 'qualificationMode': 'development_diagnostic',
            'configurationPath': str(root / 'deploy/deployment.config.json'), 'documentsRoot': str(root / 'documents'),
            'database': 'saaia_qual_' + identity.hex[:12], 'databaseUser': 'saaia_qual_' + identity.hex[:12],
            'qdrantCollection': 'ingestion_' + identity.hex,
            'endpoints': {role: 'http://127.0.0.1:' + str(19300 + n) for n, role in enumerate(('qdrant', 'tei', 'docling', 'backend'))}}


class DockerMock:
    def __init__(self, root):
        self.root, self.calls, self.network, self.containers = root, [], None, {}
        self.images = {role: 'sha256:' + str(n + 1) * 64 for n, role in enumerate(BOOT.ROLES)}
        self.fail_next_start = False

    def call(self, *args):
        self.calls.append(args)
        state = json.loads((self.root / 'owner.json').read_text())
        if args[:2] == ('network', 'ls'):
            return self.network['Id'] if self.network else ''
        if args[:2] == ('network', 'create'):
            self.network = {'Id': 'a' * 64, 'Name': state['network'], 'Labels': BOOT.labels(state),
                            'Internal': '--internal' in args, 'Driver': 'bridge', 'Containers': {}}
            return self.network['Id']
        if args[:2] == ('network', 'inspect'):
            value = copy.deepcopy(self.network)
            value['Containers'] = {key: {} for key, row in self.containers.items() if row['State']['Running']}
            return json.dumps([value])
        if args[:2] == ('image', 'inspect'):
            image = self.images['backend'] if args[2] == 'final-backend-tag' else args[2]
            return json.dumps([{'Id': image}])
        if args[0] == 'create':
            self.assert_no_unrecorded_running()
            role = next(a.split('=', 1)[1] for a in args if a.startswith('com.docker.compose.service='))
            cid = format(len(self.containers) + 1, '064x')
            env = [args[n + 1] for n, a in enumerate(args) if a == '-e']
            mounts = [{'Type': 'bind', 'Source': str(self.root / relative), 'Destination': destination,
                       'RW': not readonly} for relative, destination, readonly in BOOT.MOUNTS[role]]
            self.containers[cid] = {'Id': cid, 'Image': self.images[role],
                'Config': {'Labels': {**BOOT.labels(state), 'com.docker.compose.service': role}, 'Env': env},
                'Mounts': mounts, 'NetworkSettings': {'Networks': {state['network']: {}}},
                'State': {'Running': False, 'Status': 'created'}}
            return cid
        if args[0] == 'inspect':
            return json.dumps([self.containers[args[1]]])
        if args[0] == 'start':
            # A durable exact CID must exist before any process can start.
            assert args[1] in {r['id'] for r in state['containers']}
            if self.fail_next_start:
                self.fail_next_start = False
                raise RuntimeError('mock_start_timeout')
            self.containers[args[1]]['State'] = {'Running': True, 'Status': 'running'}
            return args[1]
        raise AssertionError('Unexpected mock Docker operation: ' + repr(args))

    def assert_no_unrecorded_running(self):
        known = {r['id'] for r in json.loads((self.root / 'owner.json').read_text())['containers']}
        assert all(cid in known for cid, row in self.containers.items() if row['State']['Running'])


class BootstrapTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.parent = Path(self.directory.name)
        self.root = self.parent / '11111111-1111-4111-8111-111111111111'
        self.catalog = self.parent / '22222222-2222-4222-8222-222222222222'
        for path in (self.root, self.catalog): path.mkdir()
        self.patch = patch.object(BOOT, 'PARENT', self.parent); self.patch.start()
        self.addCleanup(self.patch.stop); self.addCleanup(self.directory.cleanup)
        (self.root / 'owner.json').write_text(json.dumps(owner(self.root)))
        catalog = owner(self.catalog, 'manual')
        self.docker = DockerMock(self.root)
        catalog['containers'] = [{'role': role, 'service': role, 'id': str(n + 1) * 64,
                                  'imageId': self.docker.images[role]} for n, role in enumerate(BOOT.ROLES)]
        (self.catalog / 'owner.json').write_text(json.dumps(catalog))
        (self.catalog / 'deploy').mkdir()
        template = {'Database': {}, 'Auth': {}, 'Bootstrap': {}, 'Rag': {}, 'Ingestion': {},
                    'AdvancedAnalysis': {'WorkerEnabled': False, 'Provider': 'disabled',
                        'AllowExternalProviderContent': False, 'AllowExternalProviderMetadata': False},
                    'License': {'AdvancedAnalysisEnabled': False}, 'RuntimeGovernance': {'CapabilityBWorkerEnabled': False}}
        (self.catalog / 'deploy/deployment.config.json').write_text(json.dumps(template))
        for name in ('tei', 'docling'):
            path = self.root / 'cache' / name; path.mkdir(parents=True)
            (path / 'synthetic-public-cache').write_bytes(b'not real model weights; pure protocol fixture')

    def args(self, **changes):
        value = dict(root=self.root, catalog_root=self.catalog, admission_profile='scanner',
                     backend_image=None, prepare_only=True, execute=True)
        value.update(changes)
        return SimpleNamespace(**value)

    def prepare(self):
        return BOOT.run(self.args(), self.docker)

    def hashes(self):
        return {name: (self.root / name).read_bytes() for name in BOOT.FILES}

    def test_dry_run_has_no_docker_or_private_outputs(self):
        result = BOOT.run(self.args(execute=False), self.docker)
        self.assertFalse(result['executeRequested'])
        self.assertEqual([], self.docker.calls)
        self.assertFalse((self.root / 'private').exists())

    def test_prepare_only_creates_network_and_exact_four_files_but_no_container(self):
        result = self.prepare()
        self.assertTrue(result['preparationCreated'])
        self.assertEqual([], result['roles'])
        self.assertEqual('production', result['qualificationMode'])
        self.assertFalse(any(c[0] in ('create', 'start', 'run') for c in self.docker.calls))
        self.assertEqual(4, len(self.hashes()))

    def test_prepare_repeat_reuses_same_network_config_and_private_bytes(self):
        self.prepare(); frozen = self.hashes(); calls = len(self.docker.calls)
        result = self.prepare()
        self.assertFalse(result['preparationCreated'])
        self.assertEqual(frozen, self.hashes())
        self.assertFalse(any(c[:2] == ('network', 'create') for c in self.docker.calls[calls:]))

    def test_conflicting_flags_refused_before_any_effect(self):
        with self.assertRaisesRegex(ValueError, 'conflicts'):
            BOOT.run(self.args(backend_image='final-backend-tag'), self.docker)
        self.assertEqual([], self.docker.calls)

    def test_unrecorded_config_is_never_overwritten(self):
        (self.root / 'deploy').mkdir(); path = self.root / BOOT.FILES[3]; path.write_bytes(b'keep')
        with self.assertRaisesRegex(ValueError, 'overwrite'):
            self.prepare()
        self.assertEqual(b'keep', path.read_bytes())
        self.assertEqual([], self.docker.calls)

    def test_existing_container_history_is_not_requalified(self):
        state = json.loads((self.root / 'owner.json').read_text())
        state['containers'] = [{'role': 'postgres', 'service': 'postgres', 'id': 'b' * 64,
                                'imageId': self.docker.images['postgres']}]
        (self.root / 'owner.json').write_text(json.dumps(state))
        with self.assertRaisesRegex(ValueError, 'requalify'):
            self.prepare()
        self.assertEqual('development_diagnostic', json.loads((self.root / 'owner.json').read_text())['qualificationMode'])

    def test_changed_config_or_private_bytes_refuses_resume(self):
        self.prepare()
        for relative in BOOT.FILES:
            old = (self.root / relative).read_bytes()
            (self.root / relative).write_bytes(old + b'x')
            with self.subTest(relative=relative), self.assertRaisesRegex(ValueError, 'bytes_changed'):
                BOOT.run(self.args(prepare_only=False), self.docker)
            (self.root / relative).write_bytes(old)
        self.assertFalse(any(c[0] == 'create' for c in self.docker.calls))

    def test_foreign_or_replaced_network_refused(self):
        self.prepare(); self.docker.network['Labels'] = {}
        with self.assertRaisesRegex(ValueError, 'network_identity'):
            self.prepare()

    def test_missing_public_caches_refused_before_network_or_secret_creation(self):
        path = self.root / 'cache/tei/synthetic-public-cache'; path.unlink()
        with self.assertRaisesRegex(ValueError, 'model_caches'):
            self.prepare()
        self.assertEqual([], self.docker.calls)
        self.assertFalse((self.root / 'private').exists())

    def test_dependencies_reuse_frozen_bytes_and_persist_cid_before_start(self):
        self.prepare(); frozen = self.hashes()
        result = BOOT.run(self.args(prepare_only=False), self.docker)
        self.assertEqual(set(BOOT.ROLES[:-1]), set(result['roles']))
        self.assertEqual(frozen, self.hashes())
        self.assertEqual(4, len(self.docker.containers))
        BOOT.run(self.args(prepare_only=False), self.docker)
        self.assertEqual(4, len(self.docker.containers))

    def test_backend_requires_public_signature_and_is_production_without_unsigned_bypass(self):
        self.prepare(); BOOT.run(self.args(prepare_only=False), self.docker)
        with self.assertRaisesRegex(ValueError, 'signature_required'):
            BOOT.run(self.args(prepare_only=False, backend_image='final-backend-tag'), self.docker)
        (self.root / BOOT.SIGNATURE).write_bytes(base64.b64encode(bytes(64)))
        frozen = self.hashes()
        result = BOOT.run(self.args(prepare_only=False, backend_image='final-backend-tag'), self.docker)
        self.assertEqual(5, len(result['roles']))
        row = next(v for v in self.docker.containers.values() if v['Image'] == self.docker.images['backend'])
        self.assertIn('ASPNETCORE_ENVIRONMENT=Production', row['Config']['Env'])
        self.assertIn('ConfigSignature__AllowUnsignedInDevelopment=false', row['Config']['Env'])
        self.assertEqual(frozen, self.hashes())
        self.assertFalse(result['productionQualificationPassed'])
        self.assertFalse(result['cryptographicSignatureVerificationPerformedByBootstrap'])

    def test_start_interruption_leaves_exact_recorded_cid_and_retry_does_not_regenerate(self):
        self.prepare(); frozen = self.hashes(); self.docker.fail_next_start = True
        with self.assertRaisesRegex(RuntimeError, 'timeout'):
            BOOT.run(self.args(prepare_only=False), self.docker)
        state = json.loads((self.root / 'owner.json').read_text())
        self.assertEqual(1, len(state['containers']))
        self.assertFalse(self.docker.containers[state['containers'][0]['id']]['State']['Running'])
        BOOT.run(self.args(prepare_only=False), self.docker)
        self.assertEqual(4, len(self.docker.containers))
        self.assertEqual(frozen, self.hashes())

    def test_malformed_signature_and_modified_bound_signature_are_refused(self):
        self.prepare(); record = json.loads((self.root / 'owner.json').read_text())['bootstrapPreparation']
        path = self.root / BOOT.SIGNATURE
        path.write_bytes(b'notbase64!')
        with self.assertRaisesRegex(ValueError, 'not_base64'): BOOT.signature_digest(self.root, record)
        path.write_bytes(base64.b64encode(bytes(12)))
        with self.assertRaisesRegex(ValueError, 'ed25519_length'): BOOT.signature_digest(self.root, record)
        path.write_bytes(b'\xef\xbb\xbf' + base64.b64encode(bytes(64)))
        self.assertEqual(BOOT.digest(path.read_bytes()), BOOT.signature_digest(self.root, record))
        record['signatureSha256'] = BOOT.digest(path.read_bytes())
        path.write_bytes(base64.b64encode(bytes([1]) * 64))
        with self.assertRaisesRegex(ValueError, 'changed_after_binding'): BOOT.signature_digest(self.root, record)

    def test_existing_lock_refuses_a_second_operator(self):
        path = self.root / '.bootstrap-v4.lock'; path.write_bytes(b'other operator')
        with self.assertRaises(FileExistsError): self.prepare()
        self.assertEqual(b'other operator', path.read_bytes())
        self.assertEqual([], self.docker.calls)

    def test_symlinked_owned_path_is_refused_before_any_mutation(self):
        with patch.object(Path, 'is_symlink', return_value=True):
            with self.assertRaisesRegex(ValueError, 'symlink'):
                self.prepare()
        self.assertEqual([], self.docker.calls)
        self.assertFalse((self.root / 'private').exists())

    def test_persist_failure_after_create_cannot_leave_an_active_unrecorded_container(self):
        self.prepare()
        original = BOOT.persist
        def fail_after_create(root, state):
            if state['containers']:
                raise RuntimeError('mock_persist_failed')
            original(root, state)
        with patch.object(BOOT, 'persist', side_effect=fail_after_create):
            with self.assertRaisesRegex(RuntimeError, 'persist_failed'):
                BOOT.run(self.args(prepare_only=False), self.docker)
        self.assertEqual(1, len(self.docker.containers))
        self.assertTrue(all(not row['State']['Running'] for row in self.docker.containers.values()))
        self.assertFalse(any(call[0] == 'start' for call in self.docker.calls))


if __name__ == '__main__':
    unittest.main()
