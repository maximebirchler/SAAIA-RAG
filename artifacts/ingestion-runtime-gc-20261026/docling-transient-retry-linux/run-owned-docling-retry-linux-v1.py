"""Operator-only exact 26-case Linux Docling retry test; dry-run unless --execute.

No build, backend service, database, corpus, model, port or credential is used.
Only a new container of the already-built OCR campaign image may be started.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import tarfile
import time
import traceback
import xml.etree.ElementTree as ET

ROOT = Path('/home/maxime/saaia-ingestion-qualification/84cf61ff-6fb9-4fd3-a1f6-8b4b559eecff')
IMAGE = 'saaia-ocr-e2e:ocr-e2e-84cf61ff6fb9-final-v2'
RUN = 'docling-retry-84cf61ff6fb9-v1'
TEST_CLASS = 'DoclingClientTests'
METHOD_CASES = {
    'ConvertPdfAsync_transient_retry_replays_complete_multipart_then_validates_canonical_response': 3,
    'ConvertPdfAsync_transient_retry_recovers_from_network_failure': 1,
    'ConvertPdfAsync_transient_retry_keeps_original_source_handle_after_path_is_atomically_replaced': 1,
    'ConvertPdfAsync_transient_retry_bounds_persistent_network_errors_and_sanitizes_final_error_and_probes': 1,
    'ConvertPdfAsync_transient_retry_never_retries_permanent_or_unclassified_gateway_errors': 13,
    'ConvertPdfAsync_transient_retry_does_not_retry_invalid_success_responses': 3,
    'ConvertPdfAsync_transient_retry_has_four_retries_and_bounded_backoff_when_dependency_stays_unavailable': 1,
    'ConvertPdfAsync_transient_retry_preserves_caller_cancellation_during_backoff': 1,
    'ConvertPdfAsync_transient_retry_preserves_cancellation_during_a_replayed_http_request': 1,
    'ConvertPdfAsync_transient_retry_uses_one_global_options_deadline_including_previous_attempts': 1,
}
EXPECTED_CASES = 26
TIMEOUT_SECONDS = 360
BACKEND_SHA = '36d369a7ae567b73698324de1e76c078267edf386ecfd16f45a23c76513cf5a0'
TEST_SHA = '583c9042fb813784d405311e0dacdd68ec04637c9c4f4e8ce7a44b057299523f'
INVENTORY_SHA = '60d5f834a3526839991b1ec7c024cc1815541f993a8b895a0098835bd5482b72'
IMAGE_ID = re.compile(r'sha256:[0-9a-f]{64}\Z')
CID = re.compile(r'[0-9a-f]{64}\Z')
TRX_NAME = 'docling-transient-retry-linux.trx'
LABELS = {'com.saaia.validation.owner': RUN,
          'com.saaia.validation.namespace': ROOT.name,
          'com.saaia.validation.purpose': 'linux-docling-transient-retry-test'}


def require(condition, code):
    if not condition:
        raise ValueError(code)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write_new(path, value):
    with path.open('x', encoding='utf-8') as stream:
        json.dump(value, stream, indent=2)
        stream.write('\n')


def validate_trx(raw):
    root = ET.fromstring(raw)
    nodes = list(root.iter())
    by_tag = lambda tag: [node for node in nodes if node.tag.rsplit('}', 1)[-1] == tag]
    results, definitions = by_tag('UnitTestResult'), by_tag('UnitTest')
    require(len(results) == len(definitions) == EXPECTED_CASES, 'not_exactly_26_tests')
    definition_ids = [node.get('id') for node in definitions]
    result_ids = [node.get('testId') for node in results]
    require(all(definition_ids) and all(result_ids) and
            len(set(definition_ids)) == len(set(result_ids)) == EXPECTED_CASES and
            set(definition_ids) == set(result_ids), 'test_identity_mismatch')
    require(all(node.get('outcome') == 'Passed' for node in results), 'test_not_passed')
    observed = {name: 0 for name in METHOD_CASES}
    for definition in definitions:
        methods = [node for node in definition.iter() if node.tag.rsplit('}', 1)[-1] == 'TestMethod']
        require(len(methods) == 1, 'test_method_missing')
        method = methods[0]
        require(method.get('className', '').split(',', 1)[0] == TEST_CLASS and
                method.get('name') in METHOD_CASES, 'wrong_test_executed')
        observed[method.get('name')] += 1
    require(observed == METHOD_CASES, 'method_case_multiplicity_mismatch')
    counters = by_tag('Counters')
    require(len(counters) == 1, 'counters_missing')
    for key in ('total', 'executed', 'passed'):
        require(counters[0].get(key) == str(EXPECTED_CASES), 'unexpected_count_' + key)
    for key in ('failed', 'error', 'timeout', 'aborted', 'notExecuted', 'inconclusive',
                'passedButRunAborted', 'notRunnable', 'disconnected', 'warning', 'inProgress', 'pending'):
        require(counters[0].get(key, '0') == '0', 'nonzero_counter_' + key)
    return {'selected': EXPECTED_CASES, 'executed': EXPECTED_CASES, 'passed': EXPECTED_CASES,
            'failed': 0, 'notExecuted': 0, 'methodCases': observed}


def tar_member(raw, expected_name, maximum):
    require(len(raw) <= maximum + 1048576, 'copy_archive_too_large')
    with tarfile.open(fileobj=io.BytesIO(raw), mode='r:*') as archive:
        members = archive.getmembers()
        require(len(members) == 1, 'copy_archive_member_count')
        member = members[0]
        require(member.isfile() and PurePosixPath(member.name).name == expected_name and
                0 <= member.size <= maximum, 'copy_archive_not_expected_regular_file')
        stream = archive.extractfile(member)
        require(stream is not None, 'copy_archive_unreadable')
        data = stream.read(maximum + 1)
        require(len(data) == member.size, 'copy_archive_size_mismatch')
        return data


def validate_image(image, expected_id):
    require(bool(IMAGE_ID.fullmatch(expected_id)), 'invalid_expected_image_id')
    require(image.get('Id') == expected_id and image.get('Os') == 'linux' and
            image.get('Architecture') == 'amd64', 'campaign_image_identity_or_platform_mismatch')


def guard_container(container, cid, expected_id, evidence):
    require(bool(CID.fullmatch(cid)) and container.get('Id') == cid and
            container.get('Image') == expected_id, 'container_identity_mismatch')
    config, host = container.get('Config', {}), container.get('HostConfig', {})
    require(all(config.get('Labels', {}).get(k) == v for k, v in LABELS.items()), 'ownership_label_mismatch')
    require(config.get('Image') == expected_id and config.get('Entrypoint') == ['dotnet'],
            'container_image_or_entrypoint_mismatch')
    require(config.get('Cmd') == vstest_args(), 'test_command_mismatch')
    require(config.get('User', '') in ('', 'root', '0', '0:0'), 'root_test_runtime_required')
    require(host.get('NetworkMode') == 'none' and not host.get('Privileged') and
            not host.get('Devices') and not host.get('DeviceRequests') and
            not host.get('CapAdd') and not host.get('PortBindings') and
            host.get('PidMode', '') == '' and host.get('UTSMode', '') == '' and
            host.get('IpcMode', '') not in ('host',) and not host.get('VolumesFrom'), 'container_isolation_mismatch')
    require(host.get('Memory') == 1073741824 and host.get('NanoCpus') == 2000000000 and
            host.get('PidsLimit') == 256, 'container_resource_limit_mismatch')
    mounts = container.get('Mounts', [])
    require(len(mounts) == 1 and mounts[0].get('Type') == 'bind' and
            mounts[0].get('Source') == str(evidence) and mounts[0].get('Destination') == '/evidence' and
            mounts[0].get('RW') is True, 'evidence_mount_mismatch')
    require(all(not value for value in container.get('NetworkSettings', {}).get('Ports', {}).values()),
            'published_port_detected')


def vstest_args():
    return ['vstest', '/campaign/tests/SAAIA.Backend.Tests.dll',
            '--TestCaseFilter:' + '|'.join('FullyQualifiedName=' + TEST_CLASS + '.' + name for name in METHOD_CASES),
            '--logger:trx;LogFileName=' + TRX_NAME, '--ResultsDirectory:/evidence',
            '--Diag:/evidence/vstest.private.log']


def command(args, timeout=40):
    return subprocess.run(args, check=True, capture_output=True, timeout=timeout)


def inspect(cid):
    values = json.loads(command(['docker', 'inspect', cid]).stdout)
    require(len(values) == 1, 'container_inspect_count')
    return values[0]


def copy_exact(cid, source, maximum):
    raw = command(['docker', 'cp', cid + ':' + source, '-'], timeout=60).stdout
    return tar_member(raw, PurePosixPath(source).name, maximum)


def package_hashes(cid):
    inventory = copy_exact(cid, '/campaign/package-inventory.json', 1048576)
    require(sha(inventory) == INVENTORY_SHA, 'package_inventory_hash_mismatch')
    manifest = json.loads(inventory)
    expected = {'tests/SAAIA.Backend.dll': BACKEND_SHA, 'tests/SAAIA.Backend.Tests.dll': TEST_SHA}
    rows = manifest.get('files', [])
    for name, digest in expected.items():
        matches = [row for row in rows if row.get('path') == name]
        require(len(matches) == 1 and matches[0].get('sha256') == digest, 'package_assembly_anchor_mismatch')
        actual = copy_exact(cid, '/campaign/' + name, 67108864)
        require(sha(actual) == digest and len(actual) == matches[0].get('sizeBytes'), 'actual_assembly_hash_mismatch')
    linux_backend = copy_exact(cid, '/app/SAAIA.Backend.dll', 67108864)
    return {'testBackendSha256': BACKEND_SHA, 'testAssemblySha256': TEST_SHA,
            'linuxImageBackendSha256': sha(linux_backend), 'packageInventorySha256': sha(inventory)}


def no_symlinks(path):
    for value in (path, *path.parents):
        require(not value.is_symlink(), 'symlink_in_owned_path')


def run(expected_image_id):
    require(os.name == 'posix', 'operator_requires_linux')
    require(ROOT.is_dir(), 'owned_root_missing')
    no_symlinks(ROOT)
    out = ROOT / RUN
    require(not out.exists(), 'output_already_exists')
    out.mkdir(mode=0o700)
    evidence = out / 'evidence'
    evidence.mkdir(mode=0o700)
    result = {'schemaVersion': 'saaia-linux-docling-transient-retry.v1', 'executed': True, 'passed': False,
              'paidProviderCalls': 0, 'externalNetwork': False, 'buildPerformed': False, 'testClass': TEST_CLASS, 'expectedMethodCases': METHOD_CASES, 'expectedCases': EXPECTED_CASES, 'startedAtUnix': time.time(),
              'scope': 'linux_docling_client_retry_with_in_memory_http_handlers_not_full_worker_or_signed_production'}
    cid = None
    try:
        images = json.loads(command(['docker', 'image', 'inspect', IMAGE]).stdout)
        require(len(images) == 1, 'image_inspect_count')
        validate_image(images[0], expected_image_id)
        write_new(out / 'campaign-image.inspect.private.json', images[0])
        args = ['docker', 'create', '--name', RUN, '--network', 'none', '--cpus', '2',
                '--memory', '1g', '--pids-limit', '256', '--entrypoint', 'dotnet',
                '--env', 'SAAIA_TEST_OCR_E2E=0', '--env', 'COREHOST_TRACE=1',
                '--env', 'COREHOST_TRACEFILE=/evidence/corehost.private.log',
                '--mount', 'type=bind,src=' + str(evidence) + ',dst=/evidence']
        for key, value in LABELS.items():
            args.extend(['--label', key + '=' + value])
        cid = command(args + [expected_image_id] + vstest_args()).stdout.decode('ascii').strip()
        require(bool(CID.fullmatch(cid)), 'create_did_not_return_exact_id')
        result.update({'containerId': cid, 'imageId': expected_image_id})
        before = inspect(cid)
        guard_container(before, cid, expected_image_id, evidence)
        write_new(out / 'container-before.inspect.private.json', before)
        hashes = package_hashes(cid)
        with (out / 'container.private.log').open('xb') as log:
            started = subprocess.run(['docker', 'start', '--attach', cid], stdout=log,
                                     stderr=subprocess.STDOUT, timeout=TIMEOUT_SECONDS)
        after = inspect(cid)
        guard_container(after, cid, expected_image_id, evidence)
        write_new(out / 'container-after.inspect.private.json', after)
        require(not after['State']['Running'], 'container_still_running')
        trx = copy_exact(cid, '/evidence/' + TRX_NAME, 4194304)
        (out / TRX_NAME).write_bytes(trx)
        trace = copy_exact(cid, '/evidence/corehost.private.log', 67108864)
        (out / 'corehost.private.log').write_bytes(trace)
        counts = validate_trx(trx)
        require(b'/campaign/tests/SAAIA.Backend.dll' in trace and
                b'/campaign/tests/SAAIA.Backend.Tests.dll' in trace, 'snapshot_resolution_not_observed')
        require(package_hashes(cid) == hashes, 'assembly_changed_during_run')
        result.update(hashes)
        result.update({'counts': counts, 'trxSha256': sha(trx), 'corehostSha256': sha(trace),
                       'containerExitCode': after['State']['ExitCode'], 'oomKilled': after['State']['OOMKilled'],
                       'attachExitCode': started.returncode, 'networkMode': 'none',
                       'assemblyEvidence': 'exact_packaged_bytes_and_host_resolution_not_profiler_module_attestation'})
        result['passed'] = (started.returncode == 0 and after['State']['ExitCode'] == 0 and
                            not after['State']['OOMKilled'])
    except Exception as exc:
        result['failureCode'] = str(exc) if isinstance(exc, ValueError) else type(exc).__name__
        (out / 'failure.private.txt').write_text(traceback.format_exc(), encoding='utf-8')
    finally:
        if cid and CID.fullmatch(cid):
            try:
                current = inspect(cid)
                guard_container(current, cid, expected_image_id, evidence)
                if current['State']['Running']:
                    command(['docker', 'stop', '--time', '20', cid], timeout=40)
                current = inspect(cid)
                guard_container(current, cid, expected_image_id, evidence)
                write_new(out / 'container-stopped.inspect.private.json', current)
                result['ownedContainerStopped'] = not current['State']['Running']
            except Exception as exc:
                result['ownedContainerStopped'] = False
                result['cleanupFailureType'] = type(exc).__name__
        else:
            result['ownedContainerStopped'] = None
        result['passed'] = result['passed'] and result['ownedContainerStopped'] is True
        result['finishedAtUnix'] = time.time()
        write_new(out / 'operator-result.json', result)
    print(json.dumps({'executed': True, 'passed': result['passed'], 'failureCode': result.get('failureCode'),
                      'ownedContainerStopped': result['ownedContainerStopped']}))
    return 0 if result['passed'] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute', action='store_true')
    parser.add_argument('--expected-image-id', help='Immutable imageId already captured by the OCR operator proof')
    args = parser.parse_args()
    os.umask(0o077)
    if not args.execute:
        print(json.dumps({'executed': False, 'imageTag': IMAGE, 'output': str(ROOT / RUN),
                          'testClass': TEST_CLASS, 'expectedMethodCases': METHOD_CASES, 'expectedCases': EXPECTED_CASES, 'requiresExpectedImageId': True,
                          'timeoutSeconds': TIMEOUT_SECONDS, 'network': 'none', 'cpu': 2, 'memoryBytes': 1073741824}))
        return 0
    require(args.expected_image_id is not None and bool(IMAGE_ID.fullmatch(args.expected_image_id)),
            'execute_requires_explicit_immutable_image_id')
    return run(args.expected_image_id)


if __name__ == '__main__':
    raise SystemExit(main())
