"""Explicit exact-CID restart, lifecycle then failed replacement; NOEXEC by default.

No bootstrap, build, signing, installation, SSH, SQL mutation or provider call.
All operational imports, Docker, private inputs and child runners require Execute.
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
import uuid

PARENT = Path('/home/maxime/saaia-ingestion-qualification')
ROLES = ('gold-production', 'recipe-production', 'scanner', 'watcher', 'defaults')
PHASES = ('lifecycle', 'failed-replacement')
OUTPUTS = {'lifecycle': 'lifecycle-gc-final-v4', 'failed-replacement': 'failed-replacement-final-v4'}
PIN_FILE = 'production-lifecycle-final-v4-source-pins.json'


def require(value, code):
    if not value:
        raise ValueError(code)


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 4194304, 'bounded_regular_json_required')
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, 'duplicate_json_key')
            result[key] = value
        return result
    return json.loads(path.read_text(encoding='utf-8-sig'), object_pairs_hook=unique,
                      parse_constant=lambda unused: (_ for _ in ()).throw(ValueError('nonfinite_json')))


def write(path, value):
    with path.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write('\n')


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def child(root, relative):
    value = root / relative
    require(value.is_absolute() and root in value.parents and '..' not in value.parts, 'path_outside_exact_owned_root')
    for part in (value, *value.parents):
        require(not part.is_symlink(), 'symlink_in_owned_path')
    require(value.resolve() == value, 'owned_path_resolved_elsewhere')
    return value


def select_registry(registry, role):
    require(registry.get('schemaVersion') == 'saaia-isolated-production-config-signing-inputs.v1', 'registry_schema_invalid')
    rows = registry.get('prepared')
    require(isinstance(rows, list) and len(rows) == 5 and {row.get('role') for row in rows} == set(ROLES)
            and len({row.get('runId') for row in rows}) == 5, 'registry_requires_five_distinct_roles_and_namespaces')
    for row in rows:
        identity = uuid.UUID(str(row.get('runId', '')))
        require(row.get('runId') == str(identity) and row.get('root') == str(PARENT / str(identity)), 'registry_identity_invalid')
        require(re.fullmatch(r'[a-f0-9]{64}', str(row.get('configurationSha256', ''))), 'registry_configuration_hash_invalid')
        require(row.get('profile') == (row['role'] if row['role'] in ROLES[2:] else 'manual'), 'registry_profile_invalid')
    require(role == 'gold-production', 'only_synthetic_gold_lifecycle_is_authorized')
    return next(row for row in rows if row['role'] == role)


def plan_inputs(args):
    registry = args.registry
    require(registry.is_absolute() and registry.parent.parent == PARENT
            and str(uuid.UUID(registry.parent.name)) == registry.parent.name
            and registry.name == 'production-config-signing-inputs-final-v4.json', 'explicit_final_v4_catalog_registry_required')
    for part in (registry, *registry.parents):
        require(not part.is_symlink(), 'registry_symlink_forbidden')
    require(re.fullmatch(r'sha256:[a-f0-9]{64}', args.backend_image), 'observed_image_id_required_not_tag')
    require(re.fullmatch(r'[a-f0-9]{64}', args.backend_assembly_sha256), 'observed_backend_dll_hash_required')
    require(type(args.ready_seconds) is int and 30 <= args.ready_seconds <= 900, 'readiness_bound_invalid')
    row = select_registry(read(registry), args.role)
    root = Path(row['root'])
    corpus_proof = child(root, 'evidence/gold-production-final-v1/execution-result.json')
    if args.corpus_proof is not None:
        require(args.corpus_proof == corpus_proof, 'exact_new_gold_corpus_proof_required')
    result = {'schemaVersion': 'saaia-production-lifecycle-operator-plan.v1', 'executeRequested': args.execute,
              'role': row['role'], 'runId': row['runId'], 'backendImageId': args.backend_image,
              'backendAssemblySha256': args.backend_assembly_sha256, 'configurationSha256': row['configurationSha256'],
              'phaseOrder': list(PHASES), 'outputNames': list(OUTPUTS.values()), 'attemptCancelRequested': args.attempt_cancel,
              'restartExistingExactContainerIdsExplicitlyAuthorizedByExecute': args.execute,
              'newContainersCreated': False, 'paidProviderCalls': 0, 'sqlMutationPerformedByOperator': False,
              'noExecuteReadsPrivateConfigurationOwnerOrKey': False}
    return registry.parent, row, corpus_proof, result


def production_pass(proof, run_id, *, schema):
    require(proof.get('schemaVersion') == schema and proof.get('plan', {}).get('runId') == run_id,
            'phase_proof_schema_or_namespace_mismatch')
    required = ('executed', 'passed', 'deploymentQualificationPassed', 'deploymentQualificationEligible', 'configurationSignatureVerified')
    require(all(proof.get(name) is True for name in required) and proof.get('qualificationMode') == 'production'
            and proof.get('environmentObservedFromContainer') == 'Production'
            and isinstance(proof.get('finishedAtUtc'), str), 'phase_not_terminal_signed_production_pass')
    terminal = datetime.datetime.fromisoformat(proof['finishedAtUtc'].replace('Z', '+00:00'))
    require(terminal.tzinfo is not None, 'phase_terminal_timestamp_requires_timezone')
    for key in ('zeroActiveJobs', 'ownedContainersStopped', 'dockerPublishedPortBindingsAbsent'):
        require(proof.get('cleanup', {}).get(key) is True, 'phase_cleanup_not_complete')
    if schema != 'saaia-linux-ingestion-execution.v1':
        require(proof.get('cleanup', {}).get('workingSourceRestored') is True
                and proof.get('canonicalRowsSeeded') is False and proof.get('paidProviderCalls') == 0,
                'phase_source_restoration_and_no_seed_required')
    if schema == 'saaia-linux-lifecycle-execution.v1':
        require(proof.get('lateWriteReappearanceRemoved') is True
                and proof.get('generationsAndPhysicalPointIdsNeverReused') is True
                and any(step.get('searchToReadPassed') is True for step in proof.get('steps', [])),
                'real_lifecycle_gc_and_current_publication_required')


def stopped(docker):
    safe, observed = docker.inspect_all(require_running=False)
    require(len(safe) == 5 and all(item.get('running') is False for item in safe), 'all_five_exact_containers_must_be_stopped')
    require(all(item.get('State', {}).get('Status') in {'exited', 'created'} for item in observed), 'restart_state_not_admissible')
    return safe, observed


def start_ids(owner, docker, dependency_wait):
    # Complete ownership + stopped-state validation precedes the first mutation.
    stopped(docker)
    started_roles = set()
    records = {row['role']: row for row in owner['containers']}
    for role in ('postgres', 'qdrant', 'tei', 'docling', 'backend'):
        safe, _ = docker.inspect_all(require_running=False)
        require({item['role'] for item in safe if item['running']} == started_roles, 'unexpected_running_container_during_explicit_restart')
        if role == 'backend':
            readiness = dependency_wait()
        require(docker.command(['start', records[role]['id']]).strip() == records[role]['id'], 'exact_id_start_response_mismatch')
        started_roles.add(role)
    docker.inspect_all()
    return readiness


def wait_dependencies(owner, docker, bootstrap, seconds, *, clock=time.monotonic, sleep=time.sleep):
    deadline, attempts = clock() + seconds, 0
    while True:
        require(clock() < deadline, 'restart_dependencies_timeout')
        safe, _ = docker.inspect_all(require_running=False)
        require({row['role'] for row in safe if row['running']} == {'postgres', 'qdrant', 'tei', 'docling'}, 'dependency_restart_state_mismatch')
        require(clock() < deadline, 'restart_dependencies_timeout')
        pg = next(row for row in owner['containers'] if row['role'] == 'postgres')
        current = {}
        try:
            docker.command(['exec', pg['id'], 'pg_isready', '-t', '3', '-U', owner['databaseUser'], '-d', owner['database']],
                           timeout=min(5, deadline - clock()))
            current['postgres'] = True
        except (RuntimeError, OSError, subprocess.SubprocessError):
            current['postgres'] = False
        for role in ('qdrant', 'tei', 'docling'):
            require(clock() < deadline, 'restart_dependencies_timeout')
            current[role] = bootstrap.http_dependency(owner, role, deadline).get('ready') is True
        attempts += 1
        require(clock() <= deadline, 'restart_dependencies_timeout')
        if all(current.values()):
            return {'passed': True, 'attempts': attempts, 'finishedAtUtc': utc(), 'dependencyRoles': sorted(current),
                    'scope': 'health_before_backend_restart_not_conversion_warmup'}
        sleep(min(1, max(0, deadline - clock())))


def check_sources(catalog):
    pin_manifest = read(child(catalog, PIN_FILE))
    require(pin_manifest.get('schemaVersion') == 'saaia-production-lifecycle-source-pins.v1'
            and isinstance(pin_manifest.get('files'), dict) and len(pin_manifest['files']) >= 15, 'source_pins_invalid')
    for relative, expected in pin_manifest['files'].items():
        require(re.fullmatch(r'[a-f0-9]{64}', str(expected)), 'source_pin_hash_invalid')
        require(sha(child(catalog, relative)) == expected, 'frozen_operator_or_runner_changed')
    return sha(catalog / PIN_FILE)


def phase_command(catalog, root, phase, args):
    script = 'invoke-linux-lifecycle.py' if phase == 'lifecycle' else 'invoke-linux-failed-replacement.py'
    command = [sys.executable, '-B', str(child(catalog, 'tools/validation-ingestion/' + script)),
               '--owner', str(child(root, 'owner.json')), '--manifest', str(child(root, 'corpus/gold/manifest.json')),
               '--corpus-proof', str(child(root, 'evidence/gold-production-final-v1/execution-result.json')),
               '--source-directory', str(child(root, 'corpus/gold/sources')),
               '--output', str(child(root, 'evidence/' + OUTPUTS[phase])),
               '--api-key-file', str(child(root, 'private/api-key.txt')), '--maximum-wait-seconds', '300', '--execute']
    if phase == 'lifecycle' and args.attempt_cancel:
        command.append('--attempt-cancel')
    if phase == 'failed-replacement':
        command += ['--lifecycle-proof', str(child(root, 'evidence/' + OUTPUTS['lifecycle'] + '/lifecycle-result.json')),
                    '--terminal-stability-seconds', '10']
    return command


def run_phase(phase, args, row, owner, catalog, modules, operator_dir, *, process=subprocess.run):
    owned, corpus, sequential, bootstrap = modules
    root = Path(row['root'])
    docker = owned.OwnedDocker(owner, execute=True)
    require(check_sources(catalog) == args.source_pins_sha256, 'source_pins_changed_before_phase')
    require(read(child(root, 'owner.json')) == owner
            and sha(child(root, 'deploy/deployment.config.json')) == row['configurationSha256'], 'phase_owner_or_signed_configuration_drift')
    child(root, 'private/api-key.txt')
    # No stop of initially running or unowned resources: this check is before try/finally.
    stopped(docker)
    result = {'schemaVersion': 'saaia-production-lifecycle-operator-phase.v1', 'phase': phase,
              'runId': row['runId'], 'passed': False, 'startedAtUtc': utc(), 'cleanup': {}, 'paidProviderCalls': 0}
    try:
        result['dependencyReadiness'] = start_ids(owner, docker,
            lambda: wait_dependencies(owner, docker, bootstrap, args.ready_seconds))
        backend = next(record for record in owner['containers'] if record['role'] == 'backend')
        observed_dll = docker.command(['exec', backend['id'], 'sha256sum', '/app/SAAIA.Backend.dll']).split()[0]
        require(observed_dll == args.backend_assembly_sha256, 'running_backend_dll_mismatch')
        child(root, 'private/api-key.txt')
        result['readiness'] = sequential.wait_ready(owner, docker, corpus, args.ready_seconds)
        require(result['readiness'].get('configurationSignatureVerified') is True
                and result['readiness'].get('deploymentQualificationEligible') is True, 'signed_readiness_required')
        with child(root, str((operator_dir / (phase + '.runner.private.log')).relative_to(root))).open('xb') as log:
            child_process = process(phase_command(catalog, root, phase, args), stdout=log, stderr=subprocess.STDOUT,
                                    timeout=3600, check=False)
        require(child_process.returncode == 0, 'runner_process_not_successful')
        result_name = 'lifecycle-result.json' if phase == 'lifecycle' else 'failed-replacement-result.json'
        proof_path = child(root, 'evidence/' + OUTPUTS[phase] + '/' + result_name)
        proof = read(proof_path)
        production_pass(proof, row['runId'], schema='saaia-linux-' + phase + '-execution.v1')
        require(sha(child(root, 'deploy/deployment.config.json')) == row['configurationSha256'], 'configuration_changed_during_phase')
        result.update(passed=True, proofSha256=sha(proof_path), runtimeProofFile=result_name)
    except Exception as error:
        result.update(passed=False, failureType=type(error).__name__)
        write(operator_dir / (phase + '.failure.private.json'), {'type': type(error).__name__, 'message': str(error)})
    finally:
        try:
            result['cleanup'] = docker.stop()
            stopped(docker)
        except Exception as error:
            result['cleanup'].update(ownedContainersStopped=False, stopFailureType=type(error).__name__)
        try:
            result['sourceFilesStableAfterPhase'] = check_sources(catalog) == args.source_pins_sha256
        except Exception:
            result['sourceFilesStableAfterPhase'] = False
        result['passed'] = bool(result['passed'] and result['cleanup'].get('ownedContainersStopped') is True
                                and result['cleanup'].get('dockerPublishedPortBindingsAbsent') is True
                                and result['sourceFilesStableAfterPhase'] is True)
        result['finishedAtUtc'] = utc()
        write(operator_dir / (phase + '-operator-result.json'), result)
    return result


def run(args):
    catalog, row, proof_path, plan = plan_inputs(args)
    if not args.execute:
        return plan
    require(sys.platform == 'linux', 'execute_only_on_the_owned_linux_host')
    os.umask(0o077)
    pins_sha = check_sources(catalog)
    args.source_pins_sha256 = pins_sha
    root = Path(row['root'])
    owner_path = child(root, 'owner.json')
    owner = read(owner_path)
    require(owner.get('runId') == row['runId'] and owner.get('root') == row['root']
            and owner.get('qualificationMode') == 'production' and owner.get('admissionProfile') == 'manual', 'owner_registry_mismatch')
    proof = read(proof_path)
    production_pass(proof, row['runId'], schema='saaia-linux-ingestion-execution.v1')
    require(all(proof.get(name) is True for name in ('ingestionWorkerExecuted', 'sqlPublicationValidated',
            'vectorStorePublicationValidated', 'canonicalSearchValidated'))
            and 'fr-scan-critical-v2' in proof.get('plan', {}).get('caseIds', []), 'qualified_real_gold_scan_is_required')
    require(proof.get('plan', {}).get('ownerManifestSha256') == sha(owner_path)
            and proof.get('plan', {}).get('corpusManifestSha256') == sha(child(root, 'corpus/gold/manifest.json')), 'prior_gold_owner_or_manifest_changed')
    identity = read(child(root, 'evidence/gold-production-final-v1/backend-binary-identity.json'))
    require(identity.get('imageId') == args.backend_image and identity.get('assemblySha256') == args.backend_assembly_sha256
            and next(record['imageId'] for record in owner['containers'] if record['role'] == 'backend') == args.backend_image,
            'observed_image_dll_or_prior_corpus_identity_mismatch')
    require(sha(child(root, 'deploy/deployment.config.json')) == row['configurationSha256']
            == read(child(root, 'evidence/gold-production-final-v1/runtime-ownership.json')).get('signedConfigurationSha256'),
            'signed_configuration_drift')
    operator_dir = child(root, 'evidence/production-lifecycle-operator-final-v4')
    require(not operator_dir.exists() and all(not child(root, 'evidence/' + name).exists() for name in OUTPUTS.values()),
            'fresh_phase_and_operator_outputs_required_never_overwrite')
    sys.path[:0] = [str(catalog), str(child(catalog, 'tools/validation-ingestion'))]
    owned = module('lc_operator_owned', catalog / 'tools/validation-ingestion/linux_owned_runtime.py')
    corpus = module('lc_operator_corpus', catalog / 'tools/validation-ingestion/invoke-linux-ingestion.py')
    sequential = module('lc_operator_sequential', catalog / 'run-production-sequential-v4.py')
    bootstrap = module('lc_operator_bootstrap', catalog / 'bootstrap-owned-linux-v5.py')
    owned.validate_owner(owner)
    stopped(owned.OwnedDocker(owner, execute=True))
    operator_dir.mkdir(mode=0o700)
    write(operator_dir / 'execution-plan.json', {**plan, 'sourcePinsSha256': pins_sha, 'priorCorpusProofSha256': sha(proof_path)})
    results = []
    for phase in PHASES:
        result = run_phase(phase, args, row, owner, catalog, (owned, corpus, sequential, bootstrap), operator_dir)
        results.append(result)
        if not result['passed']:
            break
    summary = {'schemaVersion': 'saaia-production-lifecycle-operator.v1', 'runId': row['runId'], 'executed': True,
               'passed': len(results) == 2 and all(result['passed'] for result in results), 'results': results,
               'paidProviderCalls': 0, 'sourcePinsSha256': pins_sha}
    write(operator_dir / 'operator-result.json', summary)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--registry', type=Path, required=True)
    parser.add_argument('--role', choices=['gold-production'], required=True)
    parser.add_argument('--backend-image', required=True)
    parser.add_argument('--backend-assembly-sha256', required=True)
    parser.add_argument('--corpus-proof', type=Path)
    parser.add_argument('--ready-seconds', type=int, default=300)
    parser.add_argument('--attempt-cancel', action='store_true')
    parser.add_argument('--execute', action='store_true')
    result = run(parser.parse_args())
    print(json.dumps(result, allow_nan=False))
    return 0 if not result.get('executed') or result['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
