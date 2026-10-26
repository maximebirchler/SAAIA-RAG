"""Operator bootstrap for one explicitly owned, isolated qualification namespace.

No image pulls, production volumes, provider credentials, pruning, or reuse of DBs.
This script is campaign infrastructure, separate from product installer acceptance.
"""
import argparse
import json
import os
from pathlib import Path
import secrets
import subprocess
import uuid
from urllib.parse import urlparse

p = argparse.ArgumentParser()
p.add_argument('--root', type=Path, required=True)
p.add_argument('--catalog-root', type=Path, required=True)
p.add_argument('--backend-image')
p.add_argument('--admission-profile',choices=['manual','scanner','watcher','defaults'],default='manual')
p.add_argument('--execute', action='store_true')
a = p.parse_args()
parent = Path('/home/maxime/saaia-ingestion-qualification')
for path in [a.root, a.catalog_root]:
    assert path.is_absolute() and path.parent == parent and path.resolve() == path
    assert str(uuid.UUID(path.name)) == path.name and not path.is_symlink()
owner = json.loads((a.root / 'owner.json').read_text())
catalog = json.loads((a.catalog_root / 'owner.json').read_text())
assert owner['root'] == str(a.root) and owner['runId'] == a.root.name
assert owner['project'] == 'saaiaqual_' + uuid.UUID(owner['runId']).hex[:12]
assert owner.get('admissionProfile',a.admission_profile)==a.admission_profile
if not a.execute:
    print(json.dumps({'executeRequested': False, 'runId': owner['runId'], 'paidProviderEnabled': False}))
    raise SystemExit(0)

def docker(*args):
    result = subprocess.run(['docker', *args], capture_output=True, text=True, timeout=120)
    if result.returncode:
        raise RuntimeError('Owned Docker operation failed: ' + args[0] + ', exit=' + str(result.returncode))
    return result.stdout.strip()

def persist():
    (a.root / 'owner.json').write_text(json.dumps(owner, indent=2))

labels = ['--label', 'com.saaia.qualification.run-id=' + owner['runId'],
          '--label', 'com.docker.compose.project=' + owner['project']]
if not owner['containers']:
    docker('network', 'create', *labels, owner['network'])
    for name in ['private', 'deploy', 'documents', 'data/backend', 'data/postgres', 'data/qdrant', 'evidence']:
        (a.root / name).mkdir(parents=True, exist_ok=True)
    os.chmod(a.root / 'private', 0o700)
    private = {'postgresPassword': secrets.token_hex(24), 'apiKey': 'saaia_linux_qual_' + secrets.token_hex(24),
               'pepper': secrets.token_hex(24)}
    path = a.root / 'private/qualification-secrets.json'
    path.write_text(json.dumps(private)); os.chmod(path, 0o600)
    path = a.root / 'private/api-key.txt'
    path.write_text(private['apiKey']); os.chmod(path, 0o600)
    path = a.root / 'private/postgres.env'
    path.write_text('POSTGRES_USER=' + owner['databaseUser'] + '\nPOSTGRES_DB=' + owner['database'] +
                    '\nPOSTGRES_PASSWORD=' + private['postgresPassword'] + '\n'); os.chmod(path, 0o600)
    config = json.loads((a.catalog_root / 'deploy/deployment.config.json').read_text())
    config['Database']['ConnectionString'] = ('Host=postgres;Port=5432;Database=' + owner['database'] +
        ';Username=' + owner['databaseUser'] + ';Password=' + private['postgresPassword'] + ';Pooling=true;Maximum Pool Size=20')
    config['Auth']['Pepper'] = private['pepper']
    config['Bootstrap']['ApiKeyPlain'] = private['apiKey']
    config['Rag']['QdrantCollection'] = owner['qdrantCollection']
    profile={'manual':(False,False,1),'scanner':(True,False,2),'watcher':(False,True,2),'defaults':(True,True,2)}[a.admission_profile]
    config['Ingestion'].update({'ScannerEnabled':profile[0],'WatcherEnabled':profile[1],'WorkerConcurrency':profile[2]})
    if a.admission_profile!='manual':
        config['Ingestion'].update({'MaxFilesPerScan':1,'MinFileAgeSeconds':1,'ScanIntervalSeconds':2,'MissingGraceSeconds':5,'WatcherDebounceMs':250})
    owner['admissionProfile']=a.admission_profile
    config['VectorGc'] = {'Enabled': True, 'AllowCurrentTarget': True, 'PollSeconds': 1,
        'MaxPlansPerRound': 2, 'MaxPointsPerBatch': 64, 'RoundTimeoutSeconds': 15,
        'RecheckSeconds': 2, 'FailureBackoffSeconds': 2}
    path = Path(owner['configurationPath'])
    path.write_text(json.dumps(config, indent=2)); os.chmod(path, 0o600)

images = {item['role']: item['imageId'] for item in catalog['containers']}
env = {
 'postgres': [],
 'qdrant': [],
 'tei': ['HF_HUB_OFFLINE=1', 'RAYON_NUM_THREADS=2', 'OMP_NUM_THREADS=2'],
 'docling': ['DOCLING_DEVICE=cpu', 'DOCLING_NUM_THREADS=2', 'OMP_NUM_THREADS=2',
     'DOCLING_SERVE_ENG_KIND=local', 'DOCLING_SERVE_ENG_LOC_NUM_WORKERS=1',
     'DOCLING_SERVE_ENG_LOC_SHARE_MODELS=true', 'DOCLING_SERVE_ENABLE_UI=false',
     'DOCLING_SERVE_MAX_NUM_PAGES=1000', 'DOCLING_SERVE_MAX_FILE_SIZE=536870912',
     'DOCLING_SERVE_MAX_SYNC_WAIT=900', 'SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS=900',
     'SAAIA_DOCLING_TABLE_REPAIR_ENABLED=true'],
 'backend': ['ASPNETCORE_ENVIRONMENT=Development', 'DOTNET_ENVIRONMENT=Development',
     'ConfigSignature__ConfigPath=/app/deployment.config.json',
     'ConfigSignature__SignaturePath=/app/deploy/missing.sig',
     'ConfigSignature__AllowUnsignedInDevelopment=true', 'BACKOFFICE_LLM_ENABLED=false']}
mounts = {'postgres': [('data/postgres', '/var/lib/postgresql/data', False)],
 'qdrant': [('data/qdrant', '/qdrant/storage', False)], 'tei': [('cache/tei', '/data', False)],
 'docling': [('cache/docling', '/opt/app-root/src/.cache', False)],
 'backend': [('deploy/deployment.config.json', '/app/deployment.config.json', True),
     ('deploy', '/app/deploy', True), ('documents', '/app/documents', False), ('data/backend', '/app/data', False)]}
ports={}
for role,target_port in {'qdrant':6333,'tei':80,'docling':5001,'backend':5122}.items():
    u=urlparse(owner['endpoints'][role]); assert u.scheme=='http' and u.hostname=='127.0.0.1' and 1024<=u.port<=65535 and u.port!=5432 and not u.username and not u.password and u.path in ('','/')
    ports[role]=str(u.port)+':'+str(target_port)
assert len({v.split(':')[0] for v in ports.values()})==4
commands = {'tei': ['--model-id', 'intfloat/multilingual-e5-base', '--revision',
 'd128750597153bb5987e10b1c3493a34e5a4502a', '--huggingface-hub-cache', '/data',
 '--tokenization-workers', '2', '--max-batch-tokens', '8192', '--max-concurrent-requests', '8', '--json-output']}
roles = ['backend'] if a.backend_image else ['postgres', 'qdrant', 'tei', 'docling']
for role in roles:
    assert role not in {item['role'] for item in owner['containers']}
    image = a.backend_image if role == 'backend' else images[role]
    image_id = json.loads(docker('image', 'inspect', image))[0]['Id']
    limits = {'postgres': ('1', '1g'), 'qdrant': ('1', '1g'), 'tei': ('2', '3g'),
              'docling': ('2', '5g'), 'backend': ('2', '2g')}[role]
    args = ['run', '--pull=never', '-d', '--name', owner['project'] + '_' + role,
        '--network', owner['network'], '--network-alias', role, '--cpus', limits[0],
        '--memory', limits[1], '--pids-limit', '512', *labels,
        '--label', 'com.docker.compose.service=' + role]
    for relative, destination, readonly in mounts[role]:
        path = a.root / relative
        assert path.resolve() == path and a.root in path.parents
        args += ['--mount', 'type=bind,src=' + str(path) + ',dst=' + destination + (',readonly' if readonly else '')]
    for value in env[role]: args += ['-e', value]
    if role == 'postgres': args += ['--env-file', str(a.root / 'private/postgres.env')]
    if role in ports: args += ['-p', '127.0.0.1:' + ports[role]]
    cid = docker(*args, image_id, *commands.get(role, []))
    owner['containers'].append({'role': role, 'service': role, 'id': cid, 'imageId': image_id})
    owner['state'] = 'dependencies_started' if len(owner['containers']) < 5 else 'runtime_started'
    persist()
print(json.dumps({'runId': owner['runId'], 'roles': [item['role'] for item in owner['containers']],
                  'qualificationMode': owner['qualificationMode'], 'paidProviderEnabled': False}))
