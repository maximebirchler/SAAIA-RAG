"""Pure protocol guards only: no Docker, filesystem test, dotnet or network."""
import copy
import importlib.util
import io
import json
from pathlib import Path, PurePosixPath
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('operator_case', Path(__file__).with_name('run-owned-linux-case.py'))
operator = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(operator)
IMAGE_ID = 'sha256:' + 'a' * 64
CID = 'b' * 64


def trx(outcome='Passed', method=None, count='1', skipped='0'):
    classname, name = (method or operator.EXACT_TEST).rsplit('.', 1)
    return (f'<TestRun><Results><UnitTestResult testId="one" outcome="{outcome}"/></Results>'
            f'<TestDefinitions><UnitTest id="one"><TestMethod className="{classname}, Assembly" name="{name}"/>'
            f'</UnitTest></TestDefinitions><ResultSummary><Counters total="{count}" executed="1" '
            f'passed="1" failed="0" notExecuted="{skipped}"/></ResultSummary></TestRun>').encode()


def container():
    return {'Id': CID, 'Image': IMAGE_ID,
            'Config': {'Labels': dict(operator.LABELS), 'Image': IMAGE_ID, 'Entrypoint': ['dotnet'],
                       'Cmd': operator.vstest_args(), 'User': ''},
            'HostConfig': {'NetworkMode': 'none', 'Memory': 1073741824, 'NanoCpus': 2000000000,
                           'PidsLimit': 256, 'Devices': [], 'DeviceRequests': [], 'PortBindings': {}},
            'Mounts': [{'Type': 'bind', 'Source': '/owned/evidence', 'Destination': '/evidence', 'RW': True}],
            'NetworkSettings': {'Ports': {'5000/tcp': None}}}


def archive(name='test.trx', content=b'proof', kind=None):
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode='w') as value:
        member = tarfile.TarInfo(name)
        member.size = len(content)
        if kind is not None:
            member.type = kind
        value.addfile(member, io.BytesIO(content))
    return stream.getvalue()


class Guards(unittest.TestCase):
    def test_exact_one_pass(self):
        self.assertEqual(operator.validate_trx(trx())['passed'], 1)

    def test_failed_or_skipped_is_not_pass(self):
        for raw in (trx(outcome='Failed'), trx(outcome='NotExecuted'), trx(skipped='1')):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                operator.validate_trx(raw)

    def test_wrong_test_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'wrong_test'):
            operator.validate_trx(trx(method='Other.Class.Method'))

    def test_multiple_or_disagreeing_identity_is_rejected(self):
        for raw in (trx(count='2'), trx().replace(b'testId="one"', b'testId="other"'),
                    trx().replace(b'</Results>', b'<UnitTestResult testId="two" outcome="Passed"/></Results>')):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                operator.validate_trx(raw)

    def test_exact_container_is_accepted(self):
        operator.guard_container(container(), CID, IMAGE_ID, PurePosixPath('/owned/evidence'))

    def test_wrong_owner_id_image_or_command_rejected(self):
        mutations = [('Id', 'c' * 64), ('Image', 'sha256:' + 'c' * 64)]
        for key, value in mutations:
            row = container(); row[key] = value
            with self.assertRaises(ValueError):
                operator.guard_container(row, CID, IMAGE_ID, PurePosixPath('/owned/evidence'))
        for key, value in [('Labels', {}), ('Cmd', ['vstest', 'wrong.dll']), ('User', '1000')]:
            row = container(); row['Config'][key] = value
            with self.assertRaises(ValueError):
                operator.guard_container(row, CID, IMAGE_ID, PurePosixPath('/owned/evidence'))

    def test_network_ports_devices_and_resource_limits_rejected(self):
        for key, value in [('NetworkMode', 'bridge'), ('Privileged', True), ('Memory', 2147483648),
                           ('PidsLimit', 0), ('NanoCpus', 0), ('Devices', [{'PathOnHost': '/dev/x'}]),
                           ('PortBindings', {'80/tcp': [{'HostPort': '8080'}]}), ('PidMode', 'host')]:
            row = container(); row['HostConfig'][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                operator.guard_container(row, CID, IMAGE_ID, PurePosixPath('/owned/evidence'))

    def test_only_exact_output_bind_is_accepted(self):
        for mounts in ([], container()['Mounts'] * 2,
                       [{'Type': 'bind', 'Source': '/production', 'Destination': '/evidence', 'RW': True}]):
            row = container(); row['Mounts'] = mounts
            with self.assertRaises(ValueError):
                operator.guard_container(row, CID, IMAGE_ID, PurePosixPath('/owned/evidence'))

    def test_tar_stream_regular_exact_single_file(self):
        self.assertEqual(operator.tar_member(archive(), 'test.trx', 10), b'proof')

    def test_tar_stream_symlink_wrong_name_or_excess_rejected(self):
        for raw, maximum in ((archive(kind=tarfile.SYMTYPE), 10), (archive('wrong'), 10), (archive(), 2)):
            with self.assertRaises(ValueError):
                operator.tar_member(raw, 'test.trx', maximum)

    def test_image_identity_and_linux_platform_required(self):
        operator.validate_image({'Id': IMAGE_ID, 'Os': 'linux', 'Architecture': 'amd64'}, IMAGE_ID)
        for row in ({'Id': IMAGE_ID, 'Os': 'windows', 'Architecture': 'amd64'},
                    {'Id': 'sha256:' + 'c' * 64, 'Os': 'linux', 'Architecture': 'amd64'}):
            with self.assertRaises(ValueError):
                operator.validate_image(row, IMAGE_ID)

    def test_default_cli_is_dry_run_and_does_not_call_docker(self):
        result = subprocess.run([sys.executable, '-B', str(Path(operator.__file__))], check=True,
                                capture_output=True, text=True)
        proof = json.loads(result.stdout)
        self.assertIs(proof['executed'], False)
        self.assertEqual(proof['timeoutSeconds'], 300)
        self.assertEqual(proof['exactTest'], operator.EXACT_TEST)

    def exercise_operator(self, directory, timeout=False):
        state = {'running': False, 'started': False, 'stops': 0}
        digest = {'testBackendSha256': operator.BACKEND_SHA, 'testAssemblySha256': operator.TEST_SHA,
                  'linuxImageBackendSha256': 'c' * 64, 'packageInventorySha256': operator.INVENTORY_SHA}

        def fake_command(args, timeout=40):
            if args[:3] == ['docker', 'image', 'inspect']:
                image = {'Id': IMAGE_ID, 'Os': 'linux', 'Architecture': 'amd64'}
                return subprocess.CompletedProcess(args, 0, json.dumps([image]).encode(), b'')
            if args[:2] == ['docker', 'create']:
                return subprocess.CompletedProcess(args, 0, CID.encode(), b'')
            if args[:2] == ['docker', 'stop']:
                self.assertEqual(args[-1], CID)
                state['running'] = False; state['stops'] += 1
                return subprocess.CompletedProcess(args, 0, CID.encode(), b'')
            self.fail('unexpected command ' + repr(args))

        def fake_inspect(cid):
            self.assertEqual(cid, CID)
            row = container()
            row['Mounts'][0]['Source'] = str(directory / operator.RUN / 'evidence')
            row['State'] = {'Running': state['running'], 'ExitCode': 0, 'OOMKilled': False}
            return row

        def fake_start(args, **kwargs):
            self.assertEqual(args, ['docker', 'start', '--attach', CID])
            self.assertEqual(kwargs['timeout'], 300)
            state['started'] = True
            if timeout:
                state['running'] = True
                raise subprocess.TimeoutExpired(args, 300)
            return subprocess.CompletedProcess(args, 0)

        def fake_copy(cid, source, maximum):
            self.assertEqual(cid, CID)
            if source.endswith('.trx'):
                return trx()
            self.assertEqual(source, '/evidence/corehost.private.log')
            return b'/campaign/tests/SAAIA.Backend.dll /campaign/tests/SAAIA.Backend.Tests.dll'

        with patch.object(operator, 'ROOT', directory), patch.object(operator.os, 'name', 'posix'), \
                patch.object(operator, 'command', fake_command), patch.object(operator, 'inspect', fake_inspect), \
                patch.object(operator, 'package_hashes', return_value=digest), \
                patch.object(operator, 'copy_exact', fake_copy), patch.object(operator.subprocess, 'run', fake_start):
            code = operator.run(IMAGE_ID)
        proof = json.loads((directory / operator.RUN / 'operator-result.json').read_text())
        return code, proof, state

    def test_mock_protocol_pass_requires_stopped_exact_container(self):
        with tempfile.TemporaryDirectory() as directory:
            code, proof, state = self.exercise_operator(Path(directory))
            self.assertEqual(code, 0)
            self.assertIs(proof['passed'], True)
            self.assertIs(proof['ownedContainerStopped'], True)
            self.assertEqual(proof['counts']['executed'], 1)
            self.assertTrue(state['started'])

    def test_mock_timeout_stops_only_exact_owned_container_and_never_passes(self):
        with tempfile.TemporaryDirectory() as directory:
            code, proof, state = self.exercise_operator(Path(directory), timeout=True)
            self.assertEqual(code, 1)
            self.assertIs(proof['passed'], False)
            self.assertIs(proof['ownedContainerStopped'], True)
            self.assertEqual(proof['failureCode'], 'TimeoutExpired')
            self.assertEqual(state['stops'], 1)


if __name__ == '__main__':
    unittest.main()
