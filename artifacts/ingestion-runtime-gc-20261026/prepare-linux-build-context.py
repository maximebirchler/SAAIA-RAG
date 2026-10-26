"""Create an immutable, allowlisted build snapshot without generated/private files."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import tarfile

p = argparse.ArgumentParser()
p.add_argument('--output', type=Path, required=True)
p.add_argument('--git-ref')
a = p.parse_args()
root = Path.cwd().resolve()
assert a.output.resolve().is_relative_to(root / 'artifacts') and not a.output.exists()
required = ['.dockerignore', 'RAG.sln', 'global.json', 'Directory.Build.props',
            'backend/SAAIA.Backend/Dockerfile']
trees = ['backend/SAAIA.Backend', 'contracts/SAAIA.Contracts']
forbidden = re.compile(r'(^|/)(?:bin|obj|bin-codex[^/]*|obj-codex[^/]*|keys|__pycache__|\.git|\.vs|\.idea)(/|$)|'
    r'(^|/)(?:\.env(?:\..*)?|appsettings\.Local\.json|appsettings\..*\.local\.json|deployment\.config\.(?:json|sig)|private[^/]*\.b64)$|'
    r'\.(?:pem|key|crt|cer|pfx|p8|p12|pyc|log|tmp|bak|zip|7z|tgz|pdf|gguf|bin|safetensors|user|suo)$|\.tar\.gz$', re.I)
files = {}
if a.git_ref:
    payload = subprocess.run(['git', 'archive', '--format=tar', a.git_ref, *required, *trees],
                             check=True, stdout=subprocess.PIPE).stdout
    with tarfile.open(fileobj=io.BytesIO(payload), mode='r:') as archive:
        for item in archive.getmembers():
            if item.isfile() and not forbidden.search(item.name):
                files[item.name] = archive.extractfile(item).read()
    for name in ['.dockerignore', 'backend/SAAIA.Backend/Dockerfile']:
        files[name] = (root / name).read_bytes()
else:
    for path in [*(root / name for name in required),
                 *(path for tree in trees for path in (root / tree).rglob('*') if path.is_file())]:
        name = path.relative_to(root).as_posix()
        if not forbidden.search(name):
            assert not path.is_symlink()
            files[name] = path.read_bytes()
    # The snapshot must be stable, not a mixture of concurrent product edits.
    assert all((root / name).read_bytes() == data for name, data in files.items())
assert all(name in files for name in required)
a.output.mkdir(parents=True)
context = a.output / 'source'
context.mkdir()
records = []
for name, data in sorted(files.items()):
    path = context / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    records.append({'path': name, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
archive_path = a.output / 'build-context.tgz'
with tarfile.open(archive_path, 'w:gz') as archive:
    for record in records:
        archive.add(context / record['path'], arcname=record['path'], recursive=False)
manifest = {'schemaVersion': 'saaia-qualification-source-transfer.v1',
            'gitBaseline': a.git_ref, 'workingDockerfileOverlay': bool(a.git_ref),
            'files': records, 'archiveSha256': hashlib.sha256(archive_path.read_bytes()).hexdigest(),
            'runtimeValidationClaimed': False, 'secretsIncluded': False}
(a.output / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print(json.dumps({'files': len(records), 'archiveBytes': archive_path.stat().st_size,
                  'archiveSha256': manifest['archiveSha256']}))
