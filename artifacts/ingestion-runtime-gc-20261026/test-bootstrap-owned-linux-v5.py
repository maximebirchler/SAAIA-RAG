"""Pure health barrier tests. All Docker and HTTP objects are synthetic."""
import base64
import datetime
import importlib.util
import json
import os
from pathlib import Path
import unittest
import urllib.request
from unittest.mock import patch

HERE = Path(__file__).parent
def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module

LEGACY = load('bootstrap_v4_tests', 'test-bootstrap-owned-linux-v4.py')
BOOT = load('health_bootstrap', os.environ.get('SAAIA_BOOTSTRAP_TEST_FILE', 'bootstrap-owned-linux-v5.py'))
LEGACY.BOOT = BOOT

class DockerMock(LEGACY.DockerMock):
    def call(self, *args, **kwargs):
        if args[0] == 'exec':
            self.calls.append(args)
            return 'accepting connections'
        if args[0] == 'stop':
            self.calls.append(args)
            self.containers[args[-1]]['State'].update(Running=False, Status='exited')
            return args[-1]
        answer = super().call(*args)
        if args[0] == 'start':
            self.containers[args[1]]['State']['StartedAt'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        return answer

class Response:
    def __init__(self, status, obj): self.status, self.data = status, json.dumps(obj).encode()
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def read(self, limit): return self.data[:limit]

class HealthTests(LEGACY.BootstrapTests):
    def setUp(self):
        super().setUp()
        self.docker = DockerMock(self.root)
        self.docling = [Response(200, {'status': 'ok', 'upstream': True})]
        self.open_calls = []
        self.handlers = []
        self.clock = 0
        def opener(*handlers):
            self.handlers.extend(handlers)
            return self
        self.enterContext(patch.object(urllib.request, 'build_opener', side_effect=opener))
        if hasattr(BOOT, 'READINESS_TIMEOUT_SECONDS'):
            self.enterContext(patch.object(BOOT, 'READINESS_TIMEOUT_SECONDS', 3))
            self.enterContext(patch.object(BOOT.time, 'monotonic', side_effect=lambda: self.clock))
            self.enterContext(patch.object(BOOT.time, 'sleep', side_effect=self.sleep))

    def sleep(self, seconds): self.clock += max(seconds, 0.01)

    def open(self, url, timeout):
        self.open_calls.append(url)
        if url.endswith('/health'):
            answer = self.docling.pop(0) if len(self.docling) > 1 else self.docling[0]
            if isinstance(answer, Exception): raise answer
            return answer
        if url.endswith('/info'): return Response(200, {'model_id': 'synthetic-e5'})
        if url.endswith('/collections'): return Response(200, {'status': 'ok', 'result': {'collections': []}})
        raise AssertionError('Unexpected synthetic URL')

    def ready_dependencies(self):
        self.prepare()
        BOOT.run(self.args(prepare_only=False), self.docker)
        (self.root / BOOT.SIGNATURE).write_bytes(base64.b64encode(bytes(64)))

    def backend(self):
        return BOOT.run(self.args(prepare_only=False, backend_image='final-backend-tag'), self.docker)

    def test_running_container_with_permanent_http_503_must_not_create_backend(self):
        self.ready_dependencies()
        self.docling = [Response(503, {'status': 'starting', 'upstream': False})]
        with self.assertRaisesRegex(ValueError, 'dependency_readiness_timeout'):
            self.backend()
        self.assertEqual(4, len(self.docker.containers))
        self.assertFalse(any(r['State']['Running'] for r in self.docker.containers.values()))
        proof = json.loads((self.root / 'evidence/dependency-readiness-pre-backend.json').read_text())
        self.assertFalse(proof['passed'])

    def test_503_then_200_has_dated_four_dependency_proof_before_backend_started(self):
        self.ready_dependencies(); frozen = self.hashes()
        self.docling = [Response(503, {'status': 'starting', 'upstream': False}), Response(200, {'status': 'ok', 'upstream': True})]
        self.backend()
        proof = json.loads((self.root / BOOT.DEPENDENCY_PROOF).read_text())
        self.assertTrue(proof['passed']); self.assertEqual(2, len(proof['attempts']))
        self.assertEqual(4, len(proof['dependencyIdentities']))
        self.assertTrue(proof['proxyDisabled']); self.assertFalse(proof['redirectsAllowed'])
        self.assertEqual(frozen, self.hashes())
        chronology = json.loads((self.root / 'evidence/dependency-readiness-backend-chronology.json').read_text())
        self.assertGreaterEqual(datetime.datetime.fromisoformat(chronology['backendStartedAt']), datetime.datetime.fromisoformat(proof['finishedAtUtc']))
        self.assertNotIn('synthetic-e5', json.dumps(proof))

    def test_repeat_backend_verifies_same_proof_and_never_recreates(self):
        self.ready_dependencies(); self.backend()
        frozen = (self.root / BOOT.DEPENDENCY_PROOF).read_bytes(); count = len(self.open_calls)
        self.backend()
        self.assertEqual(5, len(self.docker.containers)); self.assertEqual(count, len(self.open_calls))
        self.assertEqual(frozen, (self.root / BOOT.DEPENDENCY_PROOF).read_bytes())

    def test_redirect_refused_and_only_exact_owned_dependencies_stopped(self):
        self.ready_dependencies()
        self.docling = [BOOT.DependencyRedirectError('dependency_redirect_forbidden')]
        with self.assertRaisesRegex(ValueError, 'redirect_forbidden'): self.backend()
        self.assertEqual(4, len(self.docker.containers))
        stopped = [c[-1] for c in self.docker.calls if c[0] == 'stop']
        self.assertEqual(set(self.docker.containers), set(stopped))

    def test_proxy_disabled_and_redirect_handler_rejects(self):
        self.ready_dependencies(); self.backend()
        proxy = [h for h in self.handlers if isinstance(h, BOOT.urllib.request.ProxyHandler)]
        self.assertTrue(proxy); self.assertTrue(all(h.proxies == {} for h in proxy))
        handler = next(h for h in self.handlers if type(h).__name__ == 'NoRedirect')
        with self.assertRaisesRegex(ValueError, 'redirect_forbidden'): handler.redirect_request(None, None, 302, '', {}, 'http://elsewhere.invalid')

    def test_http_200_upstream_false_is_not_ready(self):
        self.ready_dependencies(); self.docling = [Response(200, {'status': 'ok', 'upstream': False})]
        with self.assertRaisesRegex(ValueError, 'timeout'): self.backend()
        self.assertEqual(4, len(self.docker.containers))

    def test_replaced_dependency_identity_refused_before_health_or_backend(self):
        self.ready_dependencies()
        self.docker.containers[next(iter(self.docker.containers))]['Image'] = 'sha256:' + 'f' * 64
        with self.assertRaises(ValueError): self.backend()
        self.assertFalse(self.open_calls); self.assertEqual(4, len(self.docker.containers))

if __name__ == '__main__': unittest.main()
