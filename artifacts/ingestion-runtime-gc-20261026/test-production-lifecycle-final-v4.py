"""Offline-only fake transports; never Docker, HTTP, private store or child runner."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

SPEC = importlib.util.spec_from_file_location('production_lifecycle_operator', Path(__file__).with_name('run-production-lifecycle-final-v4.py'))
m = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m)


def owner():
    return {'containers': [{'role': role, 'id': str(index) * 64, 'imageId': 'sha256:' + 'a' * 64}
                           for index, role in enumerate(('postgres', 'qdrant', 'tei', 'docling', 'backend'), 1)],
            'databaseUser': 'saaia_qual_test', 'database': 'saaia_qual_test'}


class Docker:
    def __init__(self, states=None, fail_start=None):
        self.owner = owner()
        self.running = set(states or [])
        self.starts = []
        self.stops = 0
        self.fail_start = fail_start

    def inspect_all(self, require_running=True):
        safe = [{'role': row['role'], 'id': row['id'], 'running': row['role'] in self.running} for row in self.owner['containers']]
        if require_running and len(self.running) != 5:
            raise ValueError('not_all_running')
        observed = [{'Id': row['id'], 'State': {'Running': row['role'] in self.running,
                    'Status': 'running' if row['role'] in self.running else 'exited'}} for row in self.owner['containers']]
        return safe, observed

    def command(self, args, **unused):
        if args[0] == 'start':
            role = next(row['role'] for row in self.owner['containers'] if row['id'] == args[1])
            if self.fail_start == role:
                raise RuntimeError('synthetic_start_failure')
            self.starts.append(args[1]); self.running.add(role)
            return args[1]
        if args[0] == 'exec' and args[2] == 'sha256sum':
            return 'b' * 64 + '  /app/SAAIA.Backend.dll'
        return ''

    def stop(self):
        self.stops += 1
        self.running.clear()
        return {'ownedContainersStopped': True, 'dockerPublishedPortBindingsAbsent': True}


def proof(schema='saaia-linux-lifecycle-execution.v1'):
    return {'schemaVersion': schema, 'plan': {'runId': 'r'}, 'executed': True, 'passed': True,
            'deploymentQualificationPassed': True, 'deploymentQualificationEligible': True,
            'configurationSignatureVerified': True, 'qualificationMode': 'production',
            'environmentObservedFromContainer': 'Production', 'finishedAtUtc': '2026-10-26T12:00:00Z',
            'lateWriteReappearanceRemoved': True, 'generationsAndPhysicalPointIdsNeverReused': True,
            'canonicalRowsSeeded': False, 'paidProviderCalls': 0, 'steps': [{'searchToReadPassed': True}],
            'cleanup': {'zeroActiveJobs': True, 'ownedContainersStopped': True, 'dockerPublishedPortBindingsAbsent': True,
                        'workingSourceRestored': True}}


class OperatorTests(unittest.TestCase):
    def phase_case(self, change=None, *, exit_code=0, stop_good=True, initial_running=False, source_drift=False):
        with tempfile.TemporaryDirectory(prefix='saaia-operator-pure-') as directory:
            root = Path(directory).resolve(); (root / 'deploy').mkdir(); (root / 'evidence').mkdir()
            config = root / 'deploy/deployment.config.json'; config.write_text('{}', encoding='utf-8')
            docker = Docker(['postgres'] if initial_running else [])
            if not stop_good:
                actual_stop = docker.stop
                def incomplete_stop():
                    result = actual_stop(); result['dockerPublishedPortBindingsAbsent'] = False
                    return result
                docker.stop = incomplete_stop
            current_owner = docker.owner
            (root / 'owner.json').write_text(json.dumps(current_owner), encoding='utf-8')
            operator = root / 'evidence/operator'; operator.mkdir()
            row = {'root': str(root), 'runId': 'r', 'configurationSha256': m.sha(config)}
            args = SimpleNamespace(ready_seconds=30, backend_assembly_sha256='b' * 64, attempt_cancel=False, source_pins_sha256='d' * 64)
            owned = SimpleNamespace(OwnedDocker=lambda *unused, **kw: docker)
            sequential = SimpleNamespace(wait_ready=lambda *unused: {'configurationSignatureVerified': True, 'deploymentQualificationEligible': True})
            def fake_process(command, **unused):
                self.assertEqual({'postgres', 'qdrant', 'tei', 'docling', 'backend'}, docker.running)
                target = root / 'evidence/lifecycle-gc-final-v4'; target.mkdir()
                value = proof()
                if change is not None: change(value)
                (target / 'lifecycle-result.json').write_text(json.dumps(value), encoding='utf-8')
                return SimpleNamespace(returncode=exit_code)
            with patch.object(m, 'wait_dependencies', return_value={'passed': True}), patch.object(m, 'check_sources',
                    side_effect=['d' * 64, ('e' if source_drift else 'd') * 64]):
                if initial_running:
                    with self.assertRaises(ValueError):
                        m.run_phase('lifecycle', args, row, current_owner, root,
                                    (owned, object(), sequential, object()), operator, process=fake_process)
                    self.assertEqual(0, docker.stops)
                    self.assertEqual([], docker.starts)
                    return None
                result = m.run_phase('lifecycle', args, row, current_owner, root,
                                     (owned, object(), sequential, object()), operator, process=fake_process)
            self.assertEqual(1, docker.stops)
            self.assertEqual(set(), docker.running)
            return result

    def test_start_exact_five_ids_in_order_after_stopped_check(self):
        docker = Docker()
        def health():
            self.assertEqual({'postgres', 'qdrant', 'tei', 'docling'}, docker.running)
            return {'passed': True}
        result = m.start_ids(docker.owner, docker, health)
        self.assertTrue(result['passed'])
        self.assertEqual([row['id'] for row in docker.owner['containers']], docker.starts)

    def test_one_initial_running_container_prevents_every_start(self):
        for role in ('postgres', 'qdrant', 'tei', 'docling', 'backend'):
            docker = Docker([role])
            with self.subTest(role=role), self.assertRaises(ValueError):
                m.start_ids(docker.owner, docker, lambda: self.fail('health_called'))
            self.assertEqual([], docker.starts)

    def test_dependency_failure_never_starts_backend(self):
        docker = Docker()
        def unavailable(): raise ValueError('not_ready')
        with self.assertRaises(ValueError): m.start_ids(docker.owner, docker, unavailable)
        self.assertNotIn('backend', docker.running)
        self.assertEqual(4, len(docker.starts))

    def test_terminal_proof_rejects_false_or_string_boolean_and_missing_cleanup(self):
        for key in ('executed', 'passed', 'deploymentQualificationPassed', 'deploymentQualificationEligible', 'configurationSignatureVerified'):
            for bad in (False, 'true', None):
                value = proof(); value[key] = bad
                with self.subTest(key=key, bad=bad), self.assertRaises(ValueError):
                    m.production_pass(value, 'r', schema=value['schemaVersion'])
        for key in ('zeroActiveJobs', 'ownedContainersStopped', 'dockerPublishedPortBindingsAbsent'):
            value = proof(); value['cleanup'][key] = False
            with self.subTest(cleanup=key), self.assertRaises(ValueError):
                m.production_pass(value, 'r', schema=value['schemaVersion'])

    def test_terminal_proof_rejects_development_wrong_run_or_undated(self):
        for field, bad in (('qualificationMode', 'development_diagnostic'), ('environmentObservedFromContainer', 'Development'),
                           ('finishedAtUtc', ''), ('finishedAtUtc', '2026-10-26T12:00:00')):
            value = proof(); value[field] = bad
            with self.subTest(field=field, value=bad), self.assertRaises(ValueError):
                m.production_pass(value, 'r', schema=value['schemaVersion'])
        with self.assertRaises(ValueError): m.production_pass(proof(), 'other', schema=proof()['schemaVersion'])

    def test_valid_signed_proof_is_accepted(self):
        m.production_pass(proof(), 'r', schema=proof()['schemaVersion'])

    def test_lifecycle_specific_gc_or_source_restoration_cannot_be_omitted(self):
        for change in (lambda p: p.update(lateWriteReappearanceRemoved=False),
                       lambda p: p.update(generationsAndPhysicalPointIdsNeverReused=False),
                       lambda p: p.update(steps=[]), lambda p: p['cleanup'].update(workingSourceRestored=False)):
            value = proof(); change(value)
            with self.subTest(change=change), self.assertRaises(ValueError):
                m.production_pass(value, 'r', schema=value['schemaVersion'])

    def test_runner_exit_zero_cannot_replace_terminal_successful_proof(self):
        for change in (lambda p: p.update(executed=False), lambda p: p.update(passed=False),
                       lambda p: p['cleanup'].update(zeroActiveJobs=False), lambda p: p['plan'].update(runId='other')):
            with self.subTest(change=change): self.assertFalse(self.phase_case(change)['passed'])

    def test_failing_runner_exit_and_incomplete_operator_stop_remain_failed(self):
        self.assertFalse(self.phase_case(exit_code=1)['passed'])
        self.assertFalse(self.phase_case(stop_good=False)['passed'])

    def test_complete_phase_passes_only_after_real_stopped_reinspection(self):
        result = self.phase_case()
        self.assertTrue(result['passed'])
        self.assertTrue(result['cleanup']['ownedContainersStopped'])

    def test_source_drift_during_phase_cannot_receive_a_pass(self):
        result = self.phase_case(source_drift=True)
        self.assertFalse(result['passed'])
        self.assertFalse(result['sourceFilesStableAfterPhase'])

    def test_initial_running_phase_neither_starts_nor_stops_existing_containers(self):
        self.phase_case(initial_running=True)

    def test_phase_commands_use_new_outputs_and_only_new_lifecycle_proof(self):
        root = Path(tempfile.gettempdir()).resolve() / 'saaia-operator-synthetic'
        args = SimpleNamespace(attempt_cancel=True)
        lifecycle = m.phase_command(root / 'catalog', root, 'lifecycle', args)
        replacement = m.phase_command(root / 'catalog', root, 'failed-replacement', args)
        self.assertIn('--attempt-cancel', lifecycle)
        self.assertEqual(str(root / 'evidence/lifecycle-gc-final-v4/lifecycle-result.json'),
                         replacement[replacement.index('--lifecycle-proof') + 1])
        self.assertEqual(str(root / 'evidence/failed-replacement-final-v4'), replacement[replacement.index('--output') + 1])
        self.assertTrue(all(token not in lifecycle + replacement for token in ('compose', 'build', 'pull', 'ssh')))

    def test_noexecute_reads_only_public_registry_and_requires_observed_hashes(self):
        with tempfile.TemporaryDirectory(prefix='saaia-operator-pure-') as directory:
            parent = Path(directory).resolve()
            catalog = parent / str(uuid.uuid4()); catalog.mkdir()
            rows = []
            for role in m.ROLES:
                identity = str(uuid.uuid4())
                rows.append({'role': role, 'profile': role if role in m.ROLES[2:] else 'manual', 'runId': identity,
                             'root': str(parent / identity), 'configurationSha256': 'c' * 64})
            registry = catalog / 'production-config-signing-inputs-final-v4.json'
            registry.write_text(json.dumps({'schemaVersion': 'saaia-isolated-production-config-signing-inputs.v1', 'prepared': rows}), encoding='utf-8')
            args = SimpleNamespace(registry=registry, role='gold-production', backend_image='sha256:' + 'a' * 64,
                                   backend_assembly_sha256='b' * 64, ready_seconds=300, corpus_proof=None,
                                   execute=False, attempt_cancel=True)
            with patch.object(m, 'PARENT', parent), patch.object(m, 'module', side_effect=AssertionError('operational_import')):
                plan = m.run(args)
                self.assertFalse(plan['executeRequested'])
                self.assertEqual(list(m.PHASES), plan['phaseOrder'])
                self.assertEqual([], list(catalog.glob('evidence*')))
                for field, bad in (('backend_image', 'backend:latest'), ('backend_assembly_sha256', 'not-a-hash')):
                    changed = copy.copy(args); setattr(changed, field, bad)
                    with self.subTest(field=field), self.assertRaises(ValueError): m.run(changed)

    def test_registry_duplicate_namespace_or_wrong_profile_rejected(self):
        rows = []
        for role in m.ROLES:
            identity = str(uuid.uuid4())
            rows.append({'role': role, 'profile': role if role in m.ROLES[2:] else 'manual', 'runId': identity,
                         'root': str(m.PARENT / identity), 'configurationSha256': 'c' * 64})
        value = {'schemaVersion': 'saaia-isolated-production-config-signing-inputs.v1', 'prepared': rows}
        self.assertEqual('gold-production', m.select_registry(value, 'gold-production')['role'])
        for mode in ('duplicate', 'profile'):
            bad = copy.deepcopy(value)
            if mode == 'duplicate': bad['prepared'][1]['runId'] = bad['prepared'][0]['runId']
            else: bad['prepared'][0]['profile'] = 'scanner'
            with self.subTest(mode=mode), self.assertRaises(ValueError): m.select_registry(bad, 'gold-production')

    def test_phase_finally_stops_after_partial_start_or_readiness_failure(self):
        for failure in ('start', 'ready'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory(prefix='saaia-operator-pure-') as directory:
                root = Path(directory).resolve(); (root / 'deploy').mkdir(); (root / 'evidence').mkdir()
                config = root / 'deploy/deployment.config.json'; config.write_text('{}', encoding='utf-8')
                docker = Docker(fail_start='tei' if failure == 'start' else None)
                current_owner = docker.owner
                (root / 'owner.json').write_text(json.dumps(current_owner), encoding='utf-8')
                operator = root / 'evidence/operator'; operator.mkdir()
                row = {'root': str(root), 'runId': 'r', 'configurationSha256': m.sha(config)}
                args = SimpleNamespace(ready_seconds=30, backend_assembly_sha256='b' * 64, attempt_cancel=False, source_pins_sha256='d' * 64)
                owned = SimpleNamespace(OwnedDocker=lambda *unused, **kw: docker)
                sequential = SimpleNamespace(wait_ready=lambda *unused: (_ for _ in ()).throw(ValueError('signed_ready_failure')))
                with patch.object(m, 'wait_dependencies', return_value={'passed': True}), patch.object(m, 'check_sources', return_value='d' * 64):
                    result = m.run_phase('lifecycle', args, row, current_owner, root,
                                         (owned, object(), sequential, object()), operator,
                                         process=lambda *unused, **kw: self.fail('runner_called'))
                self.assertFalse(result['passed'])
                self.assertEqual(1, docker.stops)
                self.assertEqual(set(), docker.running)


if __name__ == '__main__':
    unittest.main(verbosity=2)
