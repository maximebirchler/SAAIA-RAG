"""Owned signed-Production qualification bootstrap, never a production installer.

Prepare once, sign the exact config bytes on the PC, transfer only the public
signature, then resume dependencies/backend without regenerating any secret.
No operation occurs on import; Docker requires --execute explicitly.
"""
import argparse
import base64
import datetime
import time
import urllib.error
import urllib.request
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import uuid
from urllib.parse import urlparse

PARENT = Path('/home/maxime/saaia-ingestion-qualification')
ROLES = ('postgres', 'qdrant', 'tei', 'docling', 'backend')
FILES = ('private/qualification-secrets.json', 'private/api-key.txt', 'private/postgres.env',
         'deploy/deployment.config.json')
SIGNATURE = 'deploy/deployment.config.sig'
PROFILE = {'manual': (False, False, 1), 'scanner': (True, False, 2),
           'watcher': (False, True, 2), 'defaults': (True, True, 2)}
MOUNTS = {'postgres': [('data/postgres', '/var/lib/postgresql/data', False)],
 'qdrant': [('data/qdrant', '/qdrant/storage', False)], 'tei': [('cache/tei', '/data', False)],
 'docling': [('cache/docling', '/opt/app-root/src/.cache', False)],
 'backend': [('deploy/deployment.config.json', '/app/deployment.config.json', True),
     ('deploy', '/app/deploy', True), ('documents', '/app/documents', False), ('data/backend', '/app/data', False)]}
ENV = {'postgres': [], 'qdrant': [],
 'tei': ['HF_HUB_OFFLINE=1', 'RAYON_NUM_THREADS=2', 'OMP_NUM_THREADS=2'],
 'docling': ['DOCLING_DEVICE=cpu', 'DOCLING_NUM_THREADS=2', 'OMP_NUM_THREADS=2',
     'DOCLING_SERVE_ENG_KIND=local', 'DOCLING_SERVE_ENG_LOC_NUM_WORKERS=1',
     'DOCLING_SERVE_ENG_LOC_SHARE_MODELS=true', 'DOCLING_SERVE_ENABLE_UI=false',
     'DOCLING_SERVE_MAX_NUM_PAGES=1000', 'DOCLING_SERVE_MAX_FILE_SIZE=536870912',
     'DOCLING_SERVE_MAX_SYNC_WAIT=900', 'SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS=900',
     'SAAIA_DOCLING_TABLE_REPAIR_ENABLED=true'],
 'backend': ['ASPNETCORE_ENVIRONMENT=Production', 'DOTNET_ENVIRONMENT=Production',
     'ConfigSignature__ConfigPath=/app/deployment.config.json',
     'ConfigSignature__SignaturePath=/app/deploy/deployment.config.sig',
     'ConfigSignature__AllowUnsignedInDevelopment=false', 'BACKOFFICE_LLM_ENABLED=false']}
LIMITS = {'postgres': ('1', '1g'), 'qdrant': ('1', '1g'), 'tei': ('2', '3g'),
          'docling': ('2', '5g'), 'backend': ('2', '2g')}
COMMANDS = {'tei': ['--model-id', 'intfloat/multilingual-e5-base', '--revision',
 'd128750597153bb5987e10b1c3493a34e5a4502a', '--huggingface-hub-cache', '/data',
 '--tokenization-workers', '2', '--max-batch-tokens', '8192', '--max-concurrent-requests', '8', '--json-output']}


def require(value, code):
    if not value:
        raise ValueError(code)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def child(root, relative):
    path = root / relative
    require(path.is_absolute() and root in path.parents and '..' not in path.parts, 'path_outside_owned_root')
    for item in (path, *path.parents):
        require(not item.is_symlink(), 'symlink_in_owned_path')
    require(path.resolve() == path, 'resolved_path_changed')
    return path


def owned_root(root):
    require(root.is_absolute() and root.parent == PARENT and str(uuid.UUID(root.name)) == root.name,
            'not_exact_uuid_qualification_root')
    child(root, 'owner.json')
    require(root.is_dir(), 'owned_root_missing')
    return root


def read_json(path):
    require(path.is_file() and path.stat().st_size <= 2097152, 'json_file_missing_or_unbounded')
    return json.loads(path.read_text(encoding='utf-8-sig'))


def json_bytes(value):
    return (json.dumps(value, indent=2, ensure_ascii=False) + '\n').encode('utf-8')


def new_file(path, data):
    with path.open('xb') as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    os.chmod(path, 0o600)


def persist(root, owner):
    target = child(root, 'owner.json')
    temporary = child(root, '.owner-v4-' + uuid.uuid4().hex + '.tmp')
    try:
        new_file(temporary, json_bytes(owner))
        os.replace(temporary, target)
    finally:
        if temporary.exists():
            temporary.unlink()


def validate_owner(root, owner, profile):
    require(owner.get('root') == str(root) and owner.get('runId') == root.name and
            owner.get('project') == 'saaiaqual_' + uuid.UUID(root.name).hex[:12], 'owner_identity_mismatch')
    require(owner.get('admissionProfile', profile) == profile and profile in PROFILE, 'admission_profile_mismatch')
    require(owner.get('configurationPath') == str(root / 'deploy/deployment.config.json') and
            owner.get('documentsRoot') == str(root / 'documents'), 'owner_layout_mismatch')
    require(owner.get('networkPolicy', 'internal') in ('internal', 'dedicated_bridge') and
            re.fullmatch(re.escape(owner['project']) + r'_[a-z0-9_]+', str(owner.get('network', ''))),
            'network_not_owned')
    records = owner.get('containers')
    require(isinstance(records, list) and len(records) <= 5 and
            len({r.get('role') for r in records}) == len(records) and
            len({r.get('id') for r in records}) == len(records), 'container_records_invalid')
    for row in records:
        require(row.get('role') in ROLES and row.get('service') == row['role'] and
                re.fullmatch(r'[a-f0-9]{64}', str(row.get('id', ''))) and
                re.fullmatch(r'sha256:[a-f0-9]{64}', str(row.get('imageId', ''))), 'invalid_container_record')
    for name in ('databaseUser', 'database'):
        require(re.fullmatch(r'saaia_qual_[a-z0-9_]{1,48}', str(owner.get(name, ''))), 'database_not_synthetic')
    require(owner.get('qdrantCollection') == 'ingestion_' + uuid.UUID(root.name).hex, 'collection_not_owned')
    require(set(owner.get('endpoints', {})) == {'qdrant', 'tei', 'docling', 'backend'}, 'endpoint_roles_invalid')
    ports = []
    for value in owner['endpoints'].values():
        url = urlparse(value)
        require(url.scheme == 'http' and url.hostname == '127.0.0.1' and url.username is None and
                url.password is None and url.path in ('', '/') and not url.query and not url.fragment and
                url.port and 1024 <= url.port <= 65535 and url.port != 5432, 'endpoint_not_loopback')
        ports.append(url.port)
    require(len(set(ports)) == 4, 'endpoint_ports_not_distinct')


class Docker:
    def call(self, *args, timeout=120):
        result = subprocess.run(['docker', '--host', 'unix:///var/run/docker.sock', *args],
                                capture_output=True, text=True, timeout=timeout)
        require(result.returncode == 0, 'owned_docker_operation_failed_' + args[0])
        return result.stdout.strip()


def labels(owner):
    return {'com.saaia.qualification.run-id': owner['runId'], 'com.docker.compose.project': owner['project']}


def inspect_network(docker, owner, *, may_create=False):
    found = docker.call('network', 'ls', '--quiet', '--no-trunc', '--filter', 'name=^' + owner['network'] + '$').splitlines()
    require(len(found) <= 1, 'network_lookup_ambiguous')
    if not found:
        require(may_create, 'prepared_network_missing')
        args = ['network', 'create']
        for key, value in labels(owner).items(): args += ['--label', key + '=' + value]
        if owner.get('networkPolicy', 'internal') == 'internal': args.append('--internal')
        docker.call(*args, owner['network'])
    observed = json.loads(docker.call('network', 'inspect', owner['network']))
    require(len(observed) == 1, 'network_inspect_count')
    network = observed[0]
    require(re.fullmatch(r'[a-f0-9]{64}', str(network.get('Id', ''))) and network.get('Name') == owner['network'] and
            all((network.get('Labels') or {}).get(k) == v for k, v in labels(owner).items()) and
            network.get('Internal') is (owner.get('networkPolicy', 'internal') == 'internal') and
            network.get('Driver') == 'bridge', 'network_identity_or_policy_mismatch')
    expected = owner.get('bootstrapPreparation', {}).get('networkId')
    require(expected is None or expected == network['Id'], 'prepared_network_replaced')
    require(set((network.get('Containers') or {})) <= {r['id'] for r in owner['containers']},
            'network_has_unrecorded_containers')
    return network


def verify_preparation(root, owner, catalog_root, profile):
    record = owner.get('bootstrapPreparation', {})
    require(record.get('schemaVersion') == 'saaia-owned-signed-bootstrap.v4' and
            record.get('catalogRoot') == str(catalog_root) and record.get('admissionProfile') == profile and
            owner.get('qualificationMode') == 'production', 'signed_preparation_required')
    require(set(record.get('files', {})) == set(FILES), 'prepared_file_inventory_invalid')
    for relative, expected in record['files'].items():
        path = child(root, relative)
        require(path.is_file() and path.stat().st_size <= 2097152 and digest(path.read_bytes()) == expected,
                'prepared_file_bytes_changed')
    return record


def prepare(root, catalog_root, owner, profile, docker):
    require(not owner['containers'], 'cannot_prepare_or_requalify_existing_containers')
    if owner.get('bootstrapPreparation'):
        verify_preparation(root, owner, catalog_root, profile)
        inspect_network(docker, owner)
        return False
    require(all(not child(root, name).exists() for name in (*FILES, SIGNATURE)), 'refuse_overwrite_unrecorded_preparation')
    catalog = read_json(child(catalog_root, 'owner.json'))
    validate_owner(catalog_root, catalog, catalog.get('admissionProfile', 'manual'))
    images = {r['role']: r['imageId'] for r in catalog['containers']}
    require(set(images) == set(ROLES), 'catalog_runtime_images_incomplete')
    template = read_json(child(catalog_root, 'deploy/deployment.config.json'))
    require(all(child(root, name).is_dir() and any(child(root, name).iterdir()) for name in ('cache/tei', 'cache/docling')),
            'prepopulated_owned_public_model_caches_required')
    advanced = template.get('AdvancedAnalysis', {})
    require(advanced.get('WorkerEnabled') is False and advanced.get('Provider') == 'disabled' and
            advanced.get('AllowExternalProviderContent') is False and advanced.get('AllowExternalProviderMetadata') is False and
            template.get('License', {}).get('AdvancedAnalysisEnabled') is False and
            template.get('RuntimeGovernance', {}).get('CapabilityBWorkerEnabled') is False,
            'template_must_explicitly_disable_llm_and_external_transmission')
    for name in ('private', 'deploy', 'documents', 'data/backend', 'data/postgres', 'data/qdrant', 'evidence'):
        child(root, name).mkdir(parents=True, exist_ok=True)
    os.chmod(child(root, 'private'), 0o700)
    network = inspect_network(docker, owner, may_create=True)
    private = {'postgresPassword': secrets.token_hex(24), 'apiKey': 'saaia_linux_qual_' + secrets.token_hex(24),
               'pepper': secrets.token_hex(24)}
    config = copy.deepcopy(template)
    config['Database']['ConnectionString'] = ('Host=postgres;Port=5432;Database=' + owner['database'] +
        ';Username=' + owner['databaseUser'] + ';Password=' + private['postgresPassword'] + ';Pooling=true;Maximum Pool Size=20')
    config['Auth']['Pepper'] = private['pepper']
    config['Bootstrap']['ApiKeyPlain'] = private['apiKey']
    config['Rag']['QdrantCollection'] = owner['qdrantCollection']
    scanner, watcher, workers = PROFILE[profile]
    config['Ingestion'].update(WorkerEnabled=True, ScannerEnabled=scanner, WatcherEnabled=watcher, WorkerConcurrency=workers)
    if profile != 'manual':
        config['Ingestion'].update(MaxFilesPerScan=1, MinFileAgeSeconds=1, ScanIntervalSeconds=2,
                                   MissingGraceSeconds=5, WatcherDebounceMs=250)
    config['VectorGc'] = {'Enabled': True, 'AllowCurrentTarget': True, 'PollSeconds': 1,
        'MaxPlansPerRound': 2, 'MaxPointsPerBatch': 64, 'RoundTimeoutSeconds': 15,
        'RecheckSeconds': 2, 'FailureBackoffSeconds': 2}
    contents = {FILES[0]: json_bytes(private), FILES[1]: private['apiKey'].encode(),
                FILES[2]: ('POSTGRES_USER=' + owner['databaseUser'] + '\nPOSTGRES_DB=' + owner['database'] +
                           '\nPOSTGRES_PASSWORD=' + private['postgresPassword'] + '\n').encode(),
                FILES[3]: json_bytes(config)}
    for name, data in contents.items(): new_file(child(root, name), data)
    owner.update(qualificationMode='production', admissionProfile=profile, state='config_prepared_waiting_signature')
    owner['bootstrapPreparation'] = {'schemaVersion': 'saaia-owned-signed-bootstrap.v4',
        'admissionProfile': profile, 'catalogRoot': str(catalog_root), 'networkId': network['Id'],
        'files': {name: digest(data) for name, data in contents.items()},
        'dependencyImages': {role: images[role] for role in ROLES if role != 'backend'},
        'signaturePath': SIGNATURE, 'cryptographicSignatureVerificationPerformedByBootstrap': False}
    persist(root, owner)
    return True


def signature_digest(root, record):
    path = child(root, SIGNATURE)
    require(path.is_file() and 0 < path.stat().st_size <= 4096, 'public_signature_required_before_backend')
    raw = path.read_bytes()
    try:
        decoded = base64.b64decode(raw.decode('utf-8-sig').strip(), validate=True)
    except (ValueError, UnicodeError):
        raise ValueError('public_signature_not_base64') from None
    require(len(decoded) == 64, 'public_signature_not_ed25519_length')
    require(record.get('signatureSha256', digest(raw)) == digest(raw), 'public_signature_changed_after_binding')
    return digest(raw)


def container_args(root, owner, role, image_id):
    args = ['create', '--pull=never', '--name', owner['project'] + '_' + role,
            '--network', owner['network'], '--network-alias', role, '--cpus', LIMITS[role][0],
            '--memory', LIMITS[role][1], '--pids-limit', '512']
    for key, value in {**labels(owner), 'com.docker.compose.service': role}.items():
        args += ['--label', key + '=' + value]
    for relative, destination, readonly in MOUNTS[role]:
        path = child(root, relative)
        require(path.exists(), 'required_mount_missing')
        args += ['--mount', 'type=bind,src=' + str(path) + ',dst=' + destination + (',readonly' if readonly else '')]
    for value in ENV[role]: args += ['-e', value]
    if role == 'postgres': args += ['--env-file', str(child(root, 'private/postgres.env'))]
    if role != 'postgres':
        target = {'qdrant': 6333, 'tei': 80, 'docling': 5001, 'backend': 5122}[role]
        args += ['-p', '127.0.0.1:' + str(urlparse(owner['endpoints'][role]).port) + ':' + str(target)]
    return args + [image_id] + COMMANDS.get(role, [])


def verify_container(root, owner, row, docker, *, require_running=True):
    result = json.loads(docker.call('inspect', row['id']))
    require(len(result) == 1, 'container_inspect_count')
    value = result[0]
    require(value.get('Id') == row['id'] and value.get('Image') == row['imageId'] and
            (not require_running or value.get('State', {}).get('Running') is True) and
            all((value.get('Config', {}).get('Labels') or {}).get(k) == v for k, v in
                {**labels(owner), 'com.docker.compose.service': row['role']}.items()), 'owned_container_identity_or_running_mismatch')
    require(set(value.get('NetworkSettings', {}).get('Networks', {})) == {owner['network']}, 'container_network_mismatch')
    mounts = value.get('Mounts', [])
    expected = {(str(child(root, p)), d, not readonly) for p, d, readonly in MOUNTS[row['role']]}
    require(len(mounts) == len(expected) and
            {(m.get('Source'), m.get('Destination'), m.get('RW')) for m in mounts} == expected and
            all(m.get('Type') == 'bind' for m in mounts), 'container_mounts_changed')
    if row['role'] == 'backend':
        env = value.get('Config', {}).get('Env', [])
        require(all(e in env for e in ENV['backend']), 'signed_production_environment_changed')
    return value


def ensure_started(root, owner, row, docker):
    value = verify_container(root, owner, row, docker, require_running=False)
    if value.get('State', {}).get('Running') is not True:
        require(value.get('State', {}).get('Status') == 'created', 'refuse_automatic_restart_of_exited_container')
        docker.call('start', row['id'])
    verify_container(root, owner, row, docker)


READINESS_TIMEOUT_SECONDS = 300
DEPENDENCY_PROOF = 'evidence/dependency-readiness-pre-backend.json'


class DependencyRedirectError(ValueError):
    pass


def http_dependency(owner, role, deadline):
    route = {'qdrant': '/collections', 'tei': '/info', 'docling': '/health'}[role]
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *unused):
            raise DependencyRedirectError('dependency_redirect_forbidden')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    remaining = deadline - time.monotonic()
    require(remaining > 0, 'dependency_readiness_timeout')
    status = None
    try:
        with opener.open(owner['endpoints'][role] + route, timeout=min(5, remaining)) as response:
            status = response.status
            raw = response.read(262145)
        if status != 200 or len(raw) > 262144:
            return {'ready': False, 'httpStatus': status, 'code': 'status_or_size_not_ready'}
        try:
            obj = json.loads(raw)
        except (ValueError, UnicodeError):
            return {'ready': False, 'httpStatus': status, 'code': 'invalid_json'}
        good = isinstance(obj, dict)
        if role == 'docling': good = good and obj.get('status') == 'ok' and obj.get('upstream') is True
        elif role == 'tei': good = good and isinstance(obj.get('model_id'), str) and bool(obj['model_id'])
        else: good = good and obj.get('status') == 'ok' and isinstance(obj.get('result'), dict) and isinstance(obj['result'].get('collections'), list)
        return {'ready': bool(good), 'httpStatus': status, 'code': 'ready' if good else 'body_not_ready'}
    except DependencyRedirectError:
        raise
    except urllib.error.HTTPError as error:
        return {'ready': False, 'httpStatus': error.code, 'code': 'http_not_ready'}
    except (urllib.error.URLError, OSError):
        return {'ready': False, 'httpStatus': status, 'code': 'transport_not_ready'}


def dependency_identities(owner):
    return sorted([{'role': r['role'], 'id': r['id'], 'imageId': r['imageId']}
                   for r in owner['containers'] if r['role'] != 'backend'], key=lambda row: row['role'])


def wait_dependencies(root, owner, docker):
    target = child(root, DEPENDENCY_PROOF)
    require(not target.exists(), 'dependency_proof_exists_use_fresh_namespace_or_recorded_backend')
    end = time.monotonic() + READINESS_TIMEOUT_SECONDS
    class DeadlineDocker:
        def call(self, *args, timeout=5):
            remaining = end - time.monotonic()
            require(remaining > 0, 'dependency_readiness_timeout')
            return docker.call(*args, timeout=min(timeout, 5, remaining))
    bounded_docker = DeadlineDocker()
    proof = {'schemaVersion': 'saaia-dependency-readiness-pre-backend.v1', 'runId': owner['runId'],
             'startedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
             'passed': False, 'backendCreated': False, 'paidProviderCalls': 0,
             'dependencyIdentities': dependency_identities(owner), 'attempts': [],
             'timeoutSeconds': READINESS_TIMEOUT_SECONDS, 'proxyDisabled': True, 'redirectsAllowed': False,
             'scope': 'dependency_health_before_backend_creation_not_full_conversion_or_runtime_qualification'}
    try:
        while True:
            require(time.monotonic() < end, 'dependency_readiness_timeout')
            inspect_network(bounded_docker, owner)
            current = {}
            for row in owner['containers']:
                require(row['role'] != 'backend', 'cannot_attest_precreation_health_after_backend_exists')
                verify_container(root, owner, row, bounded_docker)
                require(time.monotonic() < end, 'dependency_readiness_timeout')
                if row['role'] == 'postgres':
                    try:
                        docker.call('exec', row['id'], 'pg_isready', '-t', '3', '-U', owner['databaseUser'],
                                    '-d', owner['database'], timeout=min(5, max(0.1, end-time.monotonic())))
                        current['postgres'] = {'ready': True, 'code': 'accepting_connections'}
                    except (ValueError, OSError, subprocess.SubprocessError):
                        current['postgres'] = {'ready': False, 'code': 'pg_isready_not_ready'}
                else:
                    current[row['role']] = http_dependency(owner, row['role'], end)
            proof['attempts'].append({'observedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'dependencies': current})
            if (set(current) == set(ROLES[:-1]) and all(v['ready'] is True for v in current.values())
                    and time.monotonic() <= end):
                proof['passed'] = True
                return proof
            require(time.monotonic() < end, 'dependency_readiness_timeout')
            time.sleep(min(1, max(0, end-time.monotonic())))
    except Exception as error:
        proof['failureType'] = type(error).__name__
        proof['failureCode'] = str(error) if isinstance(error, ValueError) else 'dependency_probe_failed'
        raise
    finally:
        proof['finishedAtUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        new_file(target, json_bytes(proof))


def stop_after_dependency_failure(root, owner, docker):
    result = {'schemaVersion': 'saaia-dependency-readiness-stop.v1', 'runId': owner['runId'],
              'ownedDependenciesStopped': False, 'containerIds': [], 'volumesDeleted': False}
    try:
        inspect_network(docker, owner)
        for row in owner['containers']:
            require(row['role'] != 'backend', 'dependency_failure_must_precede_backend_creation')
            value = verify_container(root, owner, row, docker, require_running=False)
            if value.get('State', {}).get('Running'):
                docker.call('stop', '--time', '20', row['id'], timeout=30)
            require(verify_container(root, owner, row, docker, require_running=False).get('State', {}).get('Running') is False,
                    'owned_dependency_still_running')
            result['containerIds'].append(row['id'])
        result['ownedDependenciesStopped'] = len(result['containerIds']) == 4
    except Exception as error:
        result['stopFailureType'] = type(error).__name__
    new_file(child(root, 'evidence/dependency-readiness-pre-backend-stop.json'), json_bytes(result))


def verify_dependency_proof(root, owner):
    proof = read_json(child(root, DEPENDENCY_PROOF))
    require(proof.get('schemaVersion') == 'saaia-dependency-readiness-pre-backend.v1' and proof.get('passed') is True
            and proof.get('runId') == owner['runId'] and proof.get('backendCreated') is False
            and proof.get('dependencyIdentities') == dependency_identities(owner), 'precreation_dependency_proof_invalid')
    return proof


def verify_backend_chronology(root, owner, row, docker):
    proof = verify_dependency_proof(root, owner)
    actual = verify_container(root, owner, row, docker)
    started = datetime.datetime.fromisoformat(actual['State']['StartedAt'].replace('Z', '+00:00'))
    completed = datetime.datetime.fromisoformat(proof['finishedAtUtc'].replace('Z', '+00:00'))
    require(started.tzinfo is not None and completed.tzinfo is not None and started >= completed,
            'backend_started_before_dependency_health_proof')
    path = child(root, 'evidence/dependency-readiness-backend-chronology.json')
    expected = {'schemaVersion': 'saaia-dependency-backend-chronology.v1',
        'runId': owner['runId'], 'passed': True, 'backendId': row['id'], 'backendImageId': row['imageId'],
        'backendStartedAt': actual['State']['StartedAt'], 'dependencyReadyAtUtc': proof['finishedAtUtc'],
        'dependencyProofSha256': digest(child(root, DEPENDENCY_PROOF).read_bytes())}
    if not path.exists():
        new_file(path, json_bytes(expected))
    else:
        require(read_json(path) == expected, 'backend_chronology_proof_changed')


def start(root, catalog_root, owner, profile, backend_image, docker):
    record = verify_preparation(root, owner, catalog_root, profile)
    inspect_network(docker, owner)
    for row in owner['containers']: verify_container(root, owner, row, docker, require_running=False)
    roles = ['backend'] if backend_image else list(ROLES[:-1])
    if backend_image:
        require(set(r['role'] for r in owner['containers']) >= set(ROLES[:-1]), 'dependencies_required_before_backend')
        for row in owner['containers']:
            if row['role'] != 'backend': verify_container(root, owner, row, docker)
        record['signatureSha256'] = signature_digest(root, record)
        if any(r['role'] == 'backend' for r in owner['containers']):
            verify_dependency_proof(root, owner)
        else:
            try:
                wait_dependencies(root, owner, docker)
            except Exception:
                stop_after_dependency_failure(root, owner, docker)
                raise
    for role in roles:
        image = backend_image if role == 'backend' else record['dependencyImages'][role]
        values = json.loads(docker.call('image', 'inspect', image))
        require(len(values) == 1 and re.fullmatch(r'sha256:[a-f0-9]{64}', str(values[0].get('Id', ''))), 'image_identity_invalid')
        image_id = values[0]['Id']
        if role != 'backend': require(image_id == image, 'dependency_image_replaced')
        existing = next((r for r in owner['containers'] if r['role'] == role), None)
        if existing:
            require(existing['imageId'] == image_id, 'refuse_change_of_existing_role_image')
            ensure_started(root, owner, existing, docker)
            if role == 'backend': verify_backend_chronology(root, owner, existing, docker)
            continue
        # Validate all frozen bytes immediately before each start as well.
        verify_preparation(root, owner, catalog_root, profile)
        cid = docker.call(*container_args(root, owner, role, image_id))
        require(re.fullmatch(r'[a-f0-9]{64}', cid), 'container_start_did_not_return_full_id')
        owner['containers'].append({'role': role, 'service': role, 'id': cid, 'imageId': image_id})
        owner['state'] = 'containers_created'
        persist(root, owner)
        ensure_started(root, owner, owner['containers'][-1], docker)
        if role == 'backend': verify_backend_chronology(root, owner, owner['containers'][-1], docker)
        owner['state'] = 'runtime_started' if len(owner['containers']) == 5 else 'dependencies_started'
        persist(root, owner)


def run(args, docker=None):
    require(not (args.prepare_only and args.backend_image), 'prepare_only_conflicts_with_backend_image')
    root, catalog_root = owned_root(args.root), owned_root(args.catalog_root)
    require(root != catalog_root, 'catalog_cannot_be_target_root')
    owner = read_json(child(root, 'owner.json'))
    validate_owner(root, owner, args.admission_profile)
    if not args.execute:
        return {'executeRequested': False, 'prepareOnly': args.prepare_only, 'runId': owner['runId'],
                'privateFilesRead': False, 'containersStarted': False, 'paidProviderEnabled': False}
    os.umask(0o077)
    lock = child(root, '.bootstrap-v4.lock')
    token = secrets.token_hex(16).encode()
    new_file(lock, token)
    try:
        # Re-read while holding the exclusive single-operator lock.
        owner = read_json(child(root, 'owner.json'))
        validate_owner(root, owner, args.admission_profile)
        docker = docker or Docker()
        if args.prepare_only:
            created = prepare(root, catalog_root, owner, args.admission_profile, docker)
        else:
            start(root, catalog_root, owner, args.admission_profile, args.backend_image, docker)
            created = False
        return {'executeRequested': True, 'runId': owner['runId'], 'state': owner['state'],
                'preparationCreated': created, 'roles': [r['role'] for r in owner['containers']],
                'qualificationMode': owner['qualificationMode'],
                'configurationSha256': owner['bootstrapPreparation']['files'][FILES[3]],
                'signaturePresent': child(root, SIGNATURE).is_file(),
                'cryptographicSignatureVerificationPerformedByBootstrap': False,
                'productionQualificationPassed': False, 'paidProviderEnabled': False}
    finally:
        require(lock.is_file() and not lock.is_symlink() and lock.read_bytes() == token, 'bootstrap_lock_replaced')
        lock.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--catalog-root', type=Path, required=True)
    parser.add_argument('--backend-image')
    parser.add_argument('--admission-profile', choices=sorted(PROFILE), default='manual')
    parser.add_argument('--prepare-only', action='store_true')
    parser.add_argument('--execute', action='store_true')
    print(json.dumps(run(parser.parse_args())))


if __name__ == '__main__':
    main()
