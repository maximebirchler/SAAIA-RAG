from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import sys
import zipfile

package = Path(sys.argv[1]).resolve()
archive = Path(sys.argv[2]).resolve()
receipt = Path(sys.argv[3]).resolve()
if archive.exists() or receipt.exists():
    raise SystemExit('Preserve existing archive and receipt; choose new output paths.')
manifest = json.loads((package / 'manifest.json').read_text(encoding='utf-8-sig'))
assert manifest['schemaVersion'] == 'saaia-presentation-client.v1'
entries = {entry['path']: entry for entry in manifest['files']}
disk_files = {path.relative_to(package).as_posix(): path for path in package.rglob('*') if path.is_file()}
assert set(disk_files) == set(entries) | {'manifest.json'}
for name, entry in entries.items():
    source = disk_files[name]
    assert source.stat().st_size == entry['bytes'], name
    sha = hashlib.sha256()
    with source.open('rb') as handle:
        for chunk in iter(lambda: handle.read(8 * 1024 * 1024), b''):
            sha.update(chunk)
    assert sha.hexdigest() == entry['sha256'], name

archive.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(archive, 'x', allowZip64=True) as bundle:
    for name, source in sorted(disk_files.items()):
        method = zipfile.ZIP_STORED if name.endswith('.gguf') else zipfile.ZIP_DEFLATED
        bundle.write(source, arcname='SAAIA-Presentation/' + name, compress_type=method)
with zipfile.ZipFile(archive, 'r') as bundle:
    expected = {'SAAIA-Presentation/' + name for name in disk_files}
    assert set(bundle.namelist()) == expected
    bad = bundle.testzip()
    assert bad is None, bad
    assert bundle.getinfo('SAAIA-Presentation/manifest.json').file_size == disk_files['manifest.json'].stat().st_size
sha = hashlib.sha256()
with archive.open('rb') as handle:
    for chunk in iter(lambda: handle.read(8 * 1024 * 1024), b''):
        sha.update(chunk)
result = dict(atUtc=datetime.now(timezone.utc).isoformat(), archive=str(archive),
              bytes=archive.stat().st_size, sha256=sha.hexdigest(), files=len(disk_files),
              allSourceHashesVerified=True, allZipCrcVerified=True,
              productStatus=manifest['productStatus'])
receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(result))
