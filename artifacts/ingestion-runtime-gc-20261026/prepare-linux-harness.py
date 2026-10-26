"""Transfer only the validation Python sources and explicitly selected PDF corpus."""
import hashlib
import json
from pathlib import Path
import tarfile

root = Path.cwd().resolve()
out = root / 'artifacts/ingestion-runtime-gc-20261026/linux-harness-transfer-v1'
assert not out.exists()
out.mkdir()
files = {}
for tree in ['tools/validation-ingestion', 'tools/ingestion_gold']:
    for path in (root / tree).rglob('*.py'):
        if '__pycache__' not in path.parts and 'tests' not in path.relative_to(root / tree).parts:
            files[path.relative_to(root).as_posix()] = path
previous = root / 'artifacts/ingestion-ocr-audit-20261025'
corpora = [('gold', root / 'config/ingestion-acceptance-gold.v2.json', previous / 'gold-corpus-v2-final/sources'),
           ('recipe', previous / 'real-source-preparation-v2/manifest.private.json', previous / 'real-source-preparation-v1/sources')]
case_ids = {}
for name, manifest_path, source in corpora:
    manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    files[f'corpus/{name}/manifest.json'] = manifest_path
    case_ids[name] = []
    for case in manifest['cases']:
        path = source / (case['caseId'] + '.pdf')
        assert hashlib.sha256(path.read_bytes()).hexdigest() == case['sourceSha256']
        files[f'corpus/{name}/sources/{path.name}'] = path
        case_ids[name].append(case['caseId'])
records = [{'path': name, 'bytes': path.stat().st_size, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
           for name, path in sorted(files.items())]
archive_path = out / 'harness-and-corpus.tgz'
with tarfile.open(archive_path, 'w:gz') as archive:
    for name, path in sorted(files.items()):
        assert not path.is_symlink()
        archive.add(path, arcname=name, recursive=False)
assert all(hashlib.sha256(files[record['path']].read_bytes()).hexdigest() == record['sha256'] for record in records)
manifest = {'schemaVersion': 'saaia-linux-qualification-inputs.v1', 'files': records,
            'archiveSha256': hashlib.sha256(archive_path.read_bytes()).hexdigest(), 'caseIds': case_ids,
            'paidProviderContentTransmission': False, 'destination': 'isolated owned qualification namespace on saaia-server',
            'containsCredentials': False, 'includesOriginalCorpus': True, 'publicGitEvidenceEligible': False}
(out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print(json.dumps({'files': len(records), 'archiveBytes': archive_path.stat().st_size,
                  'archiveSha256': manifest['archiveSha256'], 'caseCounts': {k: len(v) for k, v in case_ids.items()}}))
