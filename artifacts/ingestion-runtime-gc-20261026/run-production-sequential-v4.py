"""Operator-only sequential signed Linux qualification; dry-run by default.

Never signs, builds, installs, uses SSH, alters runners, or opens a signing key.
The registry and observed backend image ID are mandatory explicit inputs.
Default selection remains watcher/defaults; every other role is explicit.
Completed manual campaigns stay
untouched unless their role is explicitly selected with a fresh output name.
"""
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from types import SimpleNamespace
import urllib.error
import urllib.request
import uuid

CAT = Path('/home/maxime/saaia-ingestion-qualification/48e5eba2-ea16-41da-98a7-65472c7f6dd6')
ORDER = ('gold-production', 'recipe-production', 'scanner', 'watcher', 'defaults')
AUTO = ('scanner', 'watcher', 'defaults')
DEFAULT_SELECTION = ('watcher', 'defaults')
PINS = {'tools/validation-ingestion/canonical_search_checks.py': '4aa3ba0ae906fefcaad7bf5fe1965f03bda96795f1157e5863ab89f0722f6266',
 'tools/validation-ingestion/linux_retrieval_fact_checks.py': 'e04b7577cf6801e2113de2a268233acafe1b296d829b54e4c973bd7906a12090',
 'bootstrap-owned-linux-v5.py': '6a5bf82d031ad85170e6337c45b2f7639a7be7b09e06441aabb9b90976589666',
 'tools/validation-ingestion/invoke-linux-automatic-ingestion.py': '87a019d8d806d3d98336c51263fbc1dbeee07a3dac0d11daab8b9621067aef7c',
 'tools/validation-ingestion/linux_automatic_ingestion_checks.py': '36d88a9643b6bf134cbf5eab752aced70c21cb5dbff41b006c1a3f63f9ba7dc0',
 'tools/validation-ingestion/invoke-linux-ingestion.py': 'eda60c2c8ff2dadfdb19e7f7f9afc4838cf3b59a6bdbdc1b055f83aa45115969',
 'tools/validation-ingestion/linux_owned_runtime.py': '013df6c287e0249002cb80154622b17a164f882fb37a13c47f863173f8fcb54d'}


def require(value, reason):
    if not value: raise ValueError(reason)


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    with path.open('x', encoding='utf-8') as stream: json.dump(value, stream, indent=2); stream.write('\n')


def read(path):
    require(path.is_file() and path.stat().st_size <= 4194304, 'bounded_json_required')
    return json.loads(path.read_text(encoding='utf-8-sig'))


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


def registry_rows(registry, selected):
    require(registry.get('schemaVersion') == 'saaia-isolated-production-config-signing-inputs.v1' and
            isinstance(registry.get('prepared'), list), 'signed_registry_schema_required')
    rows = registry['prepared']
    require({r.get('role') for r in rows} == set(ORDER) and len(rows) == 5 and
            len({r.get('runId') for r in rows}) == 5, 'registry_requires_five_distinct_owned_namespaces')
    for row in rows:
        identity = uuid.UUID(row.get('runId', ''))
        root = CAT.parent / str(identity)
        require(row.get('root') == str(root) and re.fullmatch(r'[a-f0-9]{64}', str(row.get('configurationSha256', ''))),
                'registry_root_or_configuration_hash_invalid')
        require(row.get('profile') == (row['role'] if row['role'] in AUTO else 'manual'), 'registry_profile_invalid')
    return [next(row for row in rows if row['role'] == role) for role in ORDER if role in selected]


def output_name(role):
    return 'automatic-' + role + '-production-v1' if role in AUTO else role + '-final-v1'


def defaults_preload(root, owner, manifest, boot):
    case = next(c for c in manifest['cases'] if c['caseId'] == 'fr-native-critical-v2')
    source = boot.child(root, 'corpus/gold/sources/fr-native-critical-v2.pdf')
    require(source.is_file() and sha(source) == case['sourceSha256'], 'default_preload_source_hash_mismatch')
    documents = boot.child(root, 'documents')
    target = boot.child(root, 'documents/auto-a.pdf')
    backend = next((r for r in owner['containers'] if r['role'] == 'backend'), None)
    existing = list(documents.iterdir())
    require(not existing or (existing == [target] and sha(target) == case['sourceSha256']), 'unknown_default_working_source')
    if not target.exists():
        require(backend is None, 'default_source_must_preexist_backend')
        boot.new_file(target, source.read_bytes())
    require(sha(target) == case['sourceSha256'], 'default_preload_bytes_changed')
    return {'sourceSha256': case['sourceSha256'], 'mtimeUnix': target.stat().st_mtime,
            'copiedBeforeBackendCreation': backend is None}


def wait_ready(owner, docker, corpus, seconds):
    end, attempts, headers = time.monotonic() + seconds, 0, {}
    root = Path(owner['root'])
    key_path = root / 'private/api-key.txt'
    require(key_path.is_file() and key_path.stat().st_size <= 2048, 'bounded_synthetic_admin_key_required')
    key = key_path.read_text(encoding='utf-8').strip()
    require(bool(key) and all(33 <= ord(c) <= 126 for c in key), 'synthetic_admin_key_malformed')
    headers.update(corpus.synthetic_admin_headers(key))
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args): raise ValueError('readiness_redirect_forbidden')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    while True:
        require(time.monotonic() < end, 'signed_readiness_timeout')
        _, actual = docker.inspect_all()
        attempts += 1
        try:
            remaining = end - time.monotonic()
            require(remaining > 0, 'signed_readiness_timeout')
            request = urllib.request.Request(owner['endpoints']['backend'] + '/ready', headers=headers)
            with opener.open(request, timeout=min(10, remaining)) as reply:
                require(reply.status == 200, 'ready_status_not_200')
                raw = reply.read(2097153); require(len(raw) <= 2097152, 'ready_response_unbounded')
                ready = json.loads(raw)
            backend = next(c for c in actual if c['Id'] == next(r['id'] for r in owner['containers'] if r['role'] == 'backend'))
            identity = corpus.qualification_identity(owner, backend, ready)
            details = ready.get('details', {})
            if (ready.get('ok') is True and details.get('canonical_provenance_ready') is True and
                    details.get('canonical_artifacts_enabled') is True and details.get('tei_dim') == 768):
                require(identity['deploymentQualificationEligible'] is True and details.get('config_signature_verified') is True,
                        'production_signature_not_verified')
                require(time.monotonic() <= end, 'ready_returned_after_deadline')
                return {'attempts': attempts, **identity, 'configurationSignatureVerified': True,
                        'boundWorkerConcurrency': details.get('ingestion_worker_concurrency')}
        except (urllib.error.URLError, TimeoutError, OSError):
            pass
        time.sleep(min(1, max(0, end - time.monotonic())))


def stop_exact_partial(root, boot, docker):
    owner = read(boot.child(root, 'owner.json'))
    boot.validate_owner(root, owner, owner['admissionProfile'])
    boot.inspect_network(docker, owner)
    stopped = []
    order = {'backend': 0, 'docling': 1, 'tei': 2, 'qdrant': 3, 'postgres': 4}
    for row in sorted(owner['containers'], key=lambda r: order[r['role']]):
        actual = boot.verify_container(root, owner, row, docker, require_running=False)
        if actual.get('State', {}).get('Running'): docker.call('stop', '--time', '45', row['id'])
        actual = boot.verify_container(root, owner, row, docker, require_running=False)
        require(actual.get('State', {}).get('Running') is False, 'owned_container_still_running')
        stopped.append(row['id'])
    ports_free = all(not docker.call('ps', '--quiet', '--filter', 'publish=' + str(
        __import__('urllib.parse', fromlist=['urlparse']).urlparse(url).port)) for url in owner['endpoints'].values())
    return {'ownedContainersStopped': len(stopped) == len(owner['containers']),
            'dockerPublishedPortBindingsAbsent': ports_free, 'containerCount': len(stopped),
            'containersCreatedByThisOperatorDeleted': False}


def run_phase(row, args, boot, owned, corpus, embedding):
    root, role = Path(row['root']), row['role']
    boot.owned_root(root)
    expected_profile = role if role in AUTO else 'manual'
    owner = read(boot.child(root, 'owner.json'))
    boot.validate_owner(root, owner, expected_profile)
    require(owner.get('qualificationMode') == 'production' and
            sha(boot.child(root, 'deploy/deployment.config.json')) == row['configurationSha256'], 'signed_configuration_drift')
    folder = boot.child(root, 'evidence/' + output_name(role))
    require(not folder.exists(), 'phase_output_exists_never_rerun_or_overwrite')
    operator_folder = boot.child(root, 'evidence/production-operator-' + role + '-v3')
    require(not operator_folder.exists(), 'operator_output_exists')
    operator_folder.mkdir(mode=0o700)
    result = {'schemaVersion': 'saaia-signed-production-operator-phase.v1', 'role': role,
        'runId': row['runId'], 'passed': False, 'paidProviderCalls': 0,
        'configurationSha256': row['configurationSha256'], 'cleanup': {}, 'stage': 'dependency-resume'}
    docker = boot.Docker()
    try:
        # Exact recorded dependencies already running are validated and reused.
        bootstrap_args = SimpleNamespace(root=root, catalog_root=CAT, admission_profile=expected_profile,
                                        backend_image=None, prepare_only=False, execute=True)
        recorded = {r['role']: r for r in owner['containers']}
        dependencies_running = set(boot.ROLES[:-1]) <= set(recorded)
        if dependencies_running:
            dependencies_running = all(boot.verify_container(root, owner, recorded[role], docker,
                require_running=False).get('State', {}).get('Running') is True for role in boot.ROLES[:-1])
        if not dependencies_running:
            boot.run(bootstrap_args, docker)
        corpus_name = 'recipe' if role == 'recipe-production' else 'gold'
        manifest_path = boot.child(root, 'corpus/' + corpus_name + '/manifest.json')
        manifest = read(manifest_path)
        if role == 'defaults':
            result['defaultPreload'] = defaults_preload(root, read(boot.child(root, 'owner.json')), manifest, boot)
        images = json.loads(docker.call('image', 'inspect', args.backend_image))
        require(len(images) == 1, 'backend_image_ambiguous')
        final_image_id = images[0]['Id']
        require(re.fullmatch(r'sha256:[a-f0-9]{64}', str(final_image_id)) and final_image_id == args.backend_image, 'backend_image_id_invalid_or_changed')
        result['stage'] = 'dependency-health-before-backend'
        bootstrap_args.backend_image = final_image_id
        owner = read(boot.child(root, 'owner.json'))
        existing_backend = next((r for r in owner['containers'] if r['role'] == 'backend'), None)
        # The bootstrap also verifies precreation health/chronology on a recorded backend.
        boot.run(bootstrap_args, docker)
        owner = read(boot.child(root, 'owner.json'))
        require(next(c['imageId'] for c in owner['containers'] if c['role'] == 'backend') == final_image_id,
                'backend_image_identity_changed')
        result['dependencyReadinessProofSha256'] = sha(boot.child(root, boot.DEPENDENCY_PROOF))
        result['dependencyChronologyProofSha256'] = sha(boot.child(root, 'evidence/dependency-readiness-backend-chronology.json'))
        transport = owned.OwnedDocker(owner, execute=True)
        result['stage'] = 'signed-readiness'
        result['readiness'] = wait_ready(owner, transport, corpus, args.ready_seconds)
        if role == 'defaults':
            _, actual = transport.inspect_all()
            backend = next(c for c in actual if c['Id'] == next(r['id'] for r in owner['containers'] if r['role'] == 'backend'))
            started = datetime.datetime.fromisoformat(backend['State']['StartedAt'].replace('Z', '+00:00')).timestamp()
            require(result['defaultPreload']['mtimeUnix'] <= started, 'default_preload_not_before_backend_boot')
        result['stage'] = 'frozen-runner-execution'
        common = ['--owner', str(root / 'owner.json'), '--manifest', str(manifest_path),
            '--embedding-profile', str(root / 'tei-profile-frozen-v1.json'),
            '--source-directory', str(root / 'corpus' / corpus_name / 'sources'),
            '--output', str(folder), '--api-key-file', str(root / 'private/api-key.txt'), '--execute']
        if role in AUTO:
            expected = read(boot.child(root, 'backend-binary-identity.json'))
            require(expected.get('imageId') == final_image_id, 'automatic_expected_backend_image_mismatch')
            script = CAT / 'tools/validation-ingestion/invoke-linux-automatic-ingestion.py'
            command = [sys.executable, '-B', str(script), *common, '--backend-identity',
                       str(root / 'backend-binary-identity.json'), '--admission-profile', role]
            result_name, timeout = 'automatic-ingestion-result.json', 2400
        else:
            script = CAT / 'tools/validation-ingestion/invoke-linux-ingestion.py'
            _, actual = transport.inspect_all()
            tei = next(c for c in actual if c['Id'] == next(r['id'] for r in owner['containers'] if r['role'] == 'tei'))
            profile = read(boot.child(root, 'tei-profile-frozen-v1.json'))
            binding = embedding.tei_model_binding(profile, tei)
            weight = boot.child(root, str(Path(binding['hostModelRoot']).relative_to(root) / profile['modelFileRelativePath']))
            command = [sys.executable, '-B', str(script), *common, '--model-file', str(weight), '--maximum-wait-seconds', '3600']
            for case in manifest['cases']: command += ['--case-id', case['caseId']]
            result_name, timeout = 'execution-result.json', 4200
        with (operator_folder / 'runner.private.log').open('xb') as log:
            child = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
        proof = read(boot.child(root, 'evidence/' + output_name(role) + '/' + result_name))
        require(child.returncode == 0 and proof.get('passed') is True and proof.get('deploymentQualificationPassed') is True
                and proof.get('qualificationMode') == 'production' and proof.get('configurationSignatureVerified') is True,
                'runner_not_signed_production_pass')
        result.update(passed=True, stage='completed', runtimeProofSha256=sha(folder / result_name),
                      runtimeProofName=role + '/' + result_name)
        require(sha(boot.child(root, 'deploy/deployment.config.json')) == row['configurationSha256'], 'configuration_changed_during_phase')
    except Exception as error:
        result.update(passed=False, failureType=type(error).__name__, failureStage=result['stage'])
        write(operator_folder / 'failure.private.json', {'type': type(error).__name__, 'message': str(error)})
    finally:
        try:
            result['cleanup'] = stop_exact_partial(root, boot, docker)
        except Exception as error:
            result['cleanup'] = {'stopFailureType': type(error).__name__, 'ownedContainersStopped': False}
        result['passed'] = bool(result['passed'] and result['cleanup'].get('ownedContainersStopped') is True
                                and result['cleanup'].get('dockerPublishedPortBindingsAbsent') is True)
        write(operator_folder / 'operator-result.json', result)
    return result


def validate_operator_inputs(registry, backend_image):
    require(registry.is_absolute() and registry.parent == CAT and registry.suffix == '.json'
            and registry.resolve() == registry and not registry.is_symlink(), 'explicit_owned_registry_required')
    require(re.fullmatch(r'sha256:[a-f0-9]{64}', backend_image), 'exact_observed_backend_image_id_required')
    return registry


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--registry', type=Path, required=True)
    parser.add_argument('--backend-image', required=True, help='Exact observed sha256 image ID, never an invented tag')
    parser.add_argument('--role', action='append', choices=ORDER)
    parser.add_argument('--ready-seconds', type=int, default=300)
    parser.add_argument('--execute', action='store_true')
    args = parser.parse_args()
    selected = args.role or list(DEFAULT_SELECTION)
    require(len(selected) == len(set(selected)) and 30 <= args.ready_seconds <= 900, 'invalid_selection_or_ready_bound')
    registry_path = validate_operator_inputs(args.registry, args.backend_image)
    rows = registry_rows(read(registry_path), selected)
    if not args.execute:
        print(json.dumps({'executeRequested': False, 'rolesInOrder': [r['role'] for r in rows],
                          'outputNames': [output_name(r['role']) for r in rows], 'paidProviderCalls': 0}))
        return 0
    require(sys.platform == 'linux', 'execute_only_on_owned_linux_host')
    os.umask(0o077)
    for name, expected in PINS.items(): require(sha(CAT / name) == expected, 'frozen_operator_or_runner_bytes_changed')
    sys.path.insert(0, str(CAT)); sys.path.insert(0, str(CAT / 'tools/validation-ingestion'))
    boot = load_module('signed_bootstrap_v5', CAT / 'bootstrap-owned-linux-v5.py')
    owned = load_module('sequential_owned_runtime', CAT / 'tools/validation-ingestion/linux_owned_runtime.py')
    corpus = load_module('sequential_corpus', CAT / 'tools/validation-ingestion/invoke-linux-ingestion.py')
    embedding = load_module('sequential_embedding', CAT / 'tools/validation-ingestion/embedding_attestation.py')
    results = []
    for row in rows:
        result = run_phase(row, args, boot, owned, corpus, embedding)
        results.append(result)
        print(json.dumps({'role': row['role'], 'passed': result['passed'], 'stage': result['stage'],
                          'failureType': result.get('failureType'), 'cleanup': result['cleanup']}), flush=True)
        if not result['passed']: return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
