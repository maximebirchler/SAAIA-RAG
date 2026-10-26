"""Root-operated, exact-owned Production retrieval observation; no SSH/bootstrap."""
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.request

BASE = Path('/home/maxime/saaia-ingestion-qualification')
CASES = (
    ('75599fe0-58ae-4df9-8377-d0cefc5d968a', 'gold'),
    ('2a7f47db-15f6-4399-bdd7-9f6fd44047ff', 'recipe'),
)


def main():
    if sys.argv[1:] != ['--execute']:
        print(json.dumps({'executed': False, 'roles': [c[1] for c in CASES],
                          'httpMaximum': 238, 'paidProviderCalls': 0}))
        return
    os.umask(0o077)
    prepared = []
    catalog = BASE / '48e5eba2-ea16-41da-98a7-65472c7f6dd6'
    try:
        for run_id, corpus in CASES:
            root = BASE / run_id
            assert root.is_dir() and not root.is_symlink()
            sys.path.insert(0, str(root / 'tools/validation-ingestion'))
            from linux_owned_runtime import OwnedDocker, validate_container
            owner = json.loads((root / 'owner.json').read_text())
            assert owner['runId'] == run_id and owner['qualificationMode'] == 'production'
            prior_path = root / 'evidence' / (corpus + '-production-final-v1') / 'execution-result.json'
            prior = json.loads(prior_path.read_text())
            assert prior['passed'] is True and prior['deploymentQualificationPassed'] is True
            docker = OwnedDocker(owner, execute=True)
            prepared.append((root, corpus, owner, docker))
            safe, _ = docker.inspect_all(require_running=False)
            assert len(safe) == 5 and all(not row['running'] for row in safe)
            for role in ['postgres', 'qdrant', 'tei', 'docling', 'backend']:
                row = next(row for row in owner['containers'] if row['role'] == role)
                actual = json.loads(docker.command(['inspect', row['id']]))[0]
                validate_container(owner, row, actual)
                docker.command(['start', row['id']], timeout=60)
            end = time.monotonic() + 600
            while time.monotonic() < end:
                docker.inspect_all()
                try:
                    with urllib.request.urlopen(owner['endpoints']['backend'] + '/ready', timeout=10) as response:
                        ready = json.load(response)
                    if ready.get('ok') is True:
                        assert ready.get('details', {}).get('config_signature_verified') is True
                        break
                except (OSError, ValueError):
                    pass
                time.sleep(2)
            else:
                raise RuntimeError('owned_production_readiness_timeout')
        # One shared acceptance window, created only after both stacks are ready.
        deadline = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=15)).isoformat()
        results = []
        for root, corpus, owner, docker in prepared:
            argv = ['python3', '-B', str(root / 'tools/validation-ingestion/invoke-linux-retrieval-facts.py'),
                    '--owner', str(root / 'owner.json'), '--manifest', str(root / 'corpus' / corpus / 'manifest.json'),
                    '--corpus-proof', str(root / 'evidence' / (corpus + '-production-final-v1') / 'execution-result.json'),
                    '--source-directory', str(root / 'corpus' / corpus / 'sources'),
                    '--api-key-file', str(root / 'private/api-key.txt'),
                    '--output', str(root / 'evidence' / ('retrieval-' + corpus + '-production-final-v1')),
                    '--campaign-deadline-utc', deadline]
            plan = subprocess.run(argv, capture_output=True, text=True, timeout=60)
            (root / ('retrieval-' + corpus + '-production-final-v1-plan.json')).write_text(plan.stdout)
            assert plan.returncode == 0, plan.stderr
            with (root / ('retrieval-' + corpus + '-production-final-v1-argv.json')).open('x') as stream:
                json.dump(argv + ['--execute'], stream)
            actual = subprocess.run(argv + ['--execute'], timeout=1200)
            results.append({'role': corpus, 'exitCode': actual.returncode})
            assert actual.returncode == 0
        with (catalog / 'retrieval-production-final-v1-operator-result.json').open('x') as stream:
            json.dump({'results': results, 'campaignDeadlineUtc': deadline, 'paidProviderCalls': 0}, stream)
    finally:
        cleanup_failures = []
        for root, corpus, owner, docker in reversed(prepared):
            try:
                cleanup = docker.stop()
                with (root / ('retrieval-' + corpus + '-production-final-v1-operator-cleanup.json')).open('x') as stream:
                    json.dump(cleanup, stream)
            except Exception as error:
                print(json.dumps({'cleanupRole': corpus, 'failureType': type(error).__name__}), flush=True)
                cleanup_failures.append(corpus)
        if cleanup_failures:
            raise RuntimeError('owned_cleanup_incomplete:' + ','.join(cleanup_failures))


if __name__ == '__main__':
    main()
