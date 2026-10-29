"""Causal offline tests: no PostgreSQL, Docker, SSH, provider or original corpus."""
import base64
from decimal import Decimal
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).parent))
import export_worker_publication as s


def raw(value):
    return json.dumps(value, ensure_ascii=False).encode('utf-8')


def pg_base64(data):
    value = base64.b64encode(data).decode('ascii')
    wrapped = '\n'.join(value[i:i + 76] for i in range(0, len(value), 76))
    return wrapped + ('\n' if data and len(data) % 57 == 0 else '')


def segments(data):
    if data is None: return []
    return [raw(pg_base64(data[i:i + s.CHUNK_BYTES]))
            for i in range(0, len(data), s.CHUNK_BYTES)]


class FakeSession:
    def __init__(self, mapping):
        self.mapping, self.calls = mapping, []
        self.active = False

    def query(self, sql):
        if self.active: raise AssertionError('another query started before draining the current result')
        self.active = True; self.calls.append(sql)
        try:
            yield from self.mapping[sql]
        finally:
            self.active = False


def fixture():
    q = s.templates(); mapping = {}
    def result(name, values, revision=None, page=None):
        mapping[s.query_text(q, name, revision, page)] = values
    header = {'schemaVersion': 'saaia-planning-worker-corpus-observation.v1', 'capturedAtUtc': '2026-10-29T00:00:00Z',
              'advancedJobCount': 0, 'activeJobCount': 0}
    counts = {'documents': 2, 'retrievalChunks': 2, 'units': 1, 'sections': 0, 'contentCards': 1}
    result('cell_preflight', [raw({'maximumTextBytes': 99, 'maximumJsonbStoredBytes': 123})])
    result('native_schema', [(table + '\t' + column).encode('ascii') for table, columns in s.NATIVE_COLUMNS.items() for column in sorted(columns)])
    result('header', [raw(header)]); result('counts', [raw(counts)])
    docs, expected_docs = [], []
    for index in range(2):
        revision = str(index + 1) * 8 + '-1111-4111-8111-' + str(index + 1) * 12
        doc = {'tenantId': '77777777-7777-4777-8777-777777777777', 'docId': revision,
               'revisionId': revision, 'docPath': str(index) + '.pdf', 'sourceSha256': 'a' * 64,
               'pageCount': 1, 'status': 'indexed', 'ingestionVersion': 1, 'indexedVersion': 1,
               'activeVectorPlanFingerprint': None,
               'ingestionJob': {'status': 'done', 'payload': {'nested': ['😀', None], 'exactInt': 2**100}}}
        docs.append(raw(doc)); expected_doc = dict(doc)
        data = bytes(range(256)) * (index + 1)
        artifact = {'artifactType': 'canonical_ingestion_bundle', 'schemaVersion': 'canonical.v1', 'compression': 'gzip',
                    'byteSize': 789, 'storedSize': len(data), 'contentHash': 'b' * 64}
        result('canonicalArtifact', [str(len(data)).encode() + b'\t' + raw(artifact)], revision)
        result('canonicalArtifact_payload', segments(data), revision)
        expected_doc['canonicalArtifact'] = dict(artifact, payloadBase64=pg_base64(data))
        anchors = [{'anchorId': 'A' + str(index), 'canonicalAnchor': {'hugeInt': 2**90, 'polygon': {'points': [{'x': .1, 'y': .2}]}},
                    'projectionId': None, 'precision': 'block'}]
        result('sourceAnchors', [raw(v) for v in anchors], revision); expected_doc['sourceAnchors'] = anchors
        result('nativeRawCatalog', [], revision); expected_doc['nativeRawCatalog'] = None
        page = {'tenant_id': doc['tenantId'], 'revision_id': revision, 'page_number': 1, 'reason': None,
                'block_count': None, 'stored_size': len(data), 'compression': 'gzip', 'sourceHash': 'c' * 64}
        result('nativeRawPages', [b'1\t' + str(len(data)).encode() + b'\t' + raw(page)], revision)
        result('nativeRawPages_payload', segments(data), revision, 1)
        expected_doc['nativeRawPages'] = [dict(page, payloadBase64=pg_base64(data))]
        profiles = [{'checksum': '\\x' + 'd' * 64, 'metadata': {'nested': ['été', None]}, 'keywords': []}]
        runs = [{'source_hash': '\\x' + 'a' * 64, 'payload': {'raw': 'A\r\nB'}, 'job_id': None}]
        result('profiles', [raw(v) for v in profiles], revision); expected_doc['profiles'] = profiles
        result('processingRuns', [raw(v) for v in runs], revision); expected_doc['processingRuns'] = runs
        result('document_counts', [raw({'canonicalArtifact': 1, 'sourceAnchors': 1, 'nativeRawCatalog': 0,
                                     'nativeRawPages': 1, 'profiles': 1, 'processingRuns': 1})], revision)
        expected_docs.append(expected_doc)
    result('documents', docs)
    projections = {'retrievalChunks': [{'retrievalChunkId': 'C1', 'text': 'α\r\n😀', 'metadata': {'literal': 'not rewritten'}},
                                      {'retrievalChunkId': 'foreign-historical', 'metadata': {'notFiltered': True}}],
                   'units': [{'unitId': 'U1', 'text': 'exact', 'metadata': {'n': 2**130}}], 'sections': [],
                   'contentCards': [{'contentCardId': 'CC1', 'metadata': {'null': None}}]}
    for name, values in projections.items(): result(name, [raw(v) for v in values])
    return q, FakeSession(mapping), dict(header, documents=expected_docs, **projections)


class WorkerPublicationStreamTests(unittest.TestCase):
    def test_complete_contract_is_rebuilt_without_dropping_foreign_rows_nulls_or_nested_fields(self):
        q, session, expected = fixture()
        with tempfile.TemporaryDirectory() as folder:
            count, blobs, preflight, partial = s.capture(session, Path(folder), q)
            self.assertEqual(s.parsed(partial.read_bytes()), s.parsed(raw(expected)))
            self.assertEqual(count['documents'], 2); self.assertEqual(blobs['count'], 4)
            self.assertTrue((Path(folder) / 'blobs.jsonl').is_file())
            self.assertFalse((Path(folder) / 'publication.private.json').exists())  # No premature success before ROLLBACK.
        self.assertFalse(session.active)

    def test_binary_slices_preserve_exact_pg_base64_wrapping_and_every_stored_byte(self):
        for length in (-1, 0, 1, 2, 56, 57, 58, 114, s.CHUNK_BYTES - 1, s.CHUNK_BYTES, s.CHUNK_BYTES + 1, 2 * s.CHUNK_BYTES):
            with self.subTest(length=length):
                data = None if length < 0 else bytes(i % 251 for i in range(length))
                target = io.BytesIO()
                receipt = s.append_blob(target, b'{"reason":null}', length, iter(segments(data)))
                value = json.loads(target.getvalue())
                self.assertEqual(value['payloadBase64'], None if data is None else pg_base64(data))
                if data is not None:
                    self.assertEqual(base64.b64decode(value['payloadBase64']), data)
                    self.assertEqual(receipt['sha256'], hashlib.sha256(data).hexdigest())

    def test_postgres16_observed_final_line_boundaries_are_preserved_not_trimmed(self):
        # Real PG16 READ ONLY witness: bytes -> (encoded characters, LF count,
        # terminal LF). In particular 56 bytes have padding, unlike 57 bytes.
        observed = {0: (0, 0, False), 1: (4, 0, False), 56: (76, 0, False),
                    57: (77, 1, True), 58: (81, 1, False), 114: (154, 2, True),
                    58368: (78848, 1024, True)}
        for length, expected in observed.items():
            with self.subTest(length=length):
                data = b'x' * length
                encoded = pg_base64(data)
                self.assertEqual((len(encoded), encoded.count('\n'), encoded.endswith('\n')), expected)
                target = io.BytesIO()
                s.append_blob(target, b'{"reason":null}', length, iter(segments(data)))
                self.assertEqual(json.loads(target.getvalue())['payloadBase64'], encoded)
        for length in (56, 57, 114, 58368):
            # The old last-piece rule either removed a real LF or accepted an
            # invented LF on the padded 56-byte line. Neither is admissible.
            data = b'x' * length
            wrong = pg_base64(data).rstrip('\n') if length % 57 == 0 else pg_base64(data) + '\n'
            with self.subTest(refusedLength=length), self.assertRaisesRegex(ValueError, 'base64_slice_boundary_changed'):
                s.append_blob(io.BytesIO(), b'{}', length, iter([raw(wrong)]))

    def test_missing_extra_corrupted_and_rewrapped_binary_slices_are_refused(self):
        data = b'x' * (s.CHUNK_BYTES + 1); valid = segments(data)
        for bad in (valid[:-1], valid + valid[-1:], [b'"!"'], [raw(base64.b64encode(data[:s.CHUNK_BYTES]).decode())] + valid[1:]):
            with self.subTest(bad=len(bad)), self.assertRaises(ValueError):
                s.append_blob(io.BytesIO(), b'{}', len(data), iter(bad))

    def test_number_and_text_lexemes_are_not_reserialized(self):
        original = b'{"n":1.0000000000000000000001,"big":123456789012345678901234567890,"s":"\\u00e9\\r\\n"}'
        target = io.BytesIO(); s.append_array(target, [original], 1)
        self.assertEqual(target.getvalue(), b'[' + original + b']')
        self.assertEqual(s.parsed(original)['n'], Decimal('1.0000000000000000000001'))

    def test_missing_or_duplicate_collection_row_prevents_complete_file(self):
        for replacement in ([], [b'{}', b'{}', b'{}']):
            q, session, _ = fixture(); session.mapping[s.query_text(q, 'retrievalChunks')] = replacement
            with tempfile.TemporaryDirectory() as folder, self.assertRaises(ValueError):
                s.capture(session, Path(folder), q)
            self.assertFalse(session.active)

    def test_large_text_preflight_refuses_before_documentary_json_queries(self):
        q, session, _ = fixture()
        session.mapping[s.query_text(q, 'cell_preflight')] = [raw({'maximumTextBytes': 1024**2 + 1, 'maximumJsonbStoredBytes': 1})]
        with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(ValueError, 'oversize_source_cell'):
            s.capture(session, Path(folder), q)
        self.assertEqual(len(session.calls), 1)

    def test_future_native_column_cannot_be_silently_dropped_by_explicit_projection(self):
        q, session, _ = fixture()
        session.mapping[s.query_text(q, 'native_schema')].append(b'document_revision_native_raw_catalog\tfuture_column')
        with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(ValueError, 'native_schema_changed'):
            s.capture(session, Path(folder), q)
        self.assertNotIn(s.query_text(q, 'documents'), session.calls)

    def test_sql_has_no_global_aggregate_and_binary_metadata_never_serializes_the_payload_column(self):
        q = s.templates(); all_sql = '\n'.join(q.values()).lower()
        self.assertNotIn('jsonb_agg', all_sql); self.assertNotIn('json_agg', all_sql)
        self.assertNotIn('to_jsonb(n)', all_sql)
        for name in ('canonicalArtifact_payload', 'nativeRawCatalog_payload', 'nativeRawPages_payload'):
            self.assertIn('generate_series', q[name]); self.assertIn('58368', q[name]); self.assertIn('ORDER BY s.byte_offset', q[name])
        for token in ('::text_content', 'metadata::text', 'payload::text'):
            self.assertNotIn(token, q['cell_preflight'])
        self.assertNotIn('WHERE', q['retrievalChunks']); self.assertNotIn('WHERE', q['units'])

    def test_record_reader_is_bounded_independently_of_number_of_rows(self):
        class Process:
            stdout = object()
        for count in (1, 10000):
            reader = s.PsqlSnapshot({}, Path('.'), maximum_record=32, maximum_total=50000)
            reader.process = Process()
            blocks = iter([b'{}\n' * count])
            with patch.object(s.select, 'select', return_value=([reader.process.stdout], [], [])), \
                 patch.object(s.os, 'read', side_effect=lambda *_: next(blocks, b'')):
                reader.process.stdout = Mock(fileno=lambda: 1)
                for _ in range(count): self.assertEqual(reader._line(s.time.monotonic() + 1), b'{}')
            self.assertEqual(reader.peak_record, 2)
        reader.buffer = bytearray(b'x' * 33)
        with self.assertRaisesRegex(ValueError, 'record_size_limit'): reader._line(s.time.monotonic() + 1)

    def test_popen_start_failure_closes_open_files(self):
        owner = {'containers': [{'role': 'postgres', 'id': 'a' * 64}], 'databaseUser': 'fixture', 'database': 'fixture'}
        with tempfile.TemporaryDirectory() as folder:
            reader = s.PsqlSnapshot(owner, Path(folder))
            with patch.object(s.subprocess, 'Popen', side_effect=OSError('start_failed')):
                with self.assertRaisesRegex(OSError, 'start_failed'): reader.__enter__()
            self.assertTrue(reader.errors.closed); self.assertTrue(reader.transcript.closed)

    def test_actual_session_requests_one_repeatable_read_read_only_transaction_and_rolls_back(self):
        class Process:
            def __init__(self): self.stdin, self.stdout, self.returncode = io.BytesIO(), io.BytesIO(), None
            def poll(self): return self.returncode
            def wait(self, timeout): self.returncode = 0; return 0
        owner = {'containers': [{'role': 'postgres', 'id': 'a' * 64}], 'databaseUser': 'fixture', 'database': 'fixture'}
        identity = raw({'readOnly': 'on', 'isolation': 'repeatable read', 'snapshot': '1:2:', 'serverMajor': 16,
                        'applicationName': 'saaia_worker_publication_stream', 'backendPid': 1234})
        with tempfile.TemporaryDirectory() as folder:
            reader = s.PsqlSnapshot(owner, Path(folder)); process = Process()
            with patch.object(s.subprocess, 'Popen', return_value=process) as popen, patch.object(reader, 'query', return_value=iter([identity])) as query:
                with reader: pass
            sql = query.call_args.args[0]
            self.assertEqual(sql.count('BEGIN '), 1)
            self.assertIn('BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY', sql)
            self.assertIn('PGAPPNAME=saaia_worker_publication_stream', popen.call_args.args[0])
            self.assertNotIn('SET LOCAL application_name', sql)
            self.assertIn('FETCH_COUNT=1', popen.call_args.args[0])
            self.assertIn('pager=off', popen.call_args.args[0])
            self.assertTrue(reader.close_state['rollbackClientExitZero'])

    def test_normal_worker_admission_stays_strict_when_a_dependency_is_dead(self):
        with tempfile.TemporaryDirectory() as folder:
            owner = {'root': str(Path(folder).resolve())}; docker = Mock(owner=owner)
            docker.inspect_all.side_effect = ValueError('dead_dependency')
            with patch.object(s, 'validate_owner', return_value=owner), patch.object(s, 'PsqlSnapshot') as session:
                with self.assertRaisesRegex(ValueError, 'dead_dependency'):
                    s.capture_owned_publication(docker, Path(folder).resolve() / 'fresh', 'a' * 64)
                docker.inspect_all.assert_called_once_with(require_running=True)
                session.assert_not_called()

    def test_future_worker_uses_stream_capture_instead_of_the_global_aggregate(self):
        source = Path(__file__).with_name('invoke_owned_linux_planning_ingestion.py').read_text(encoding='utf-8')
        self.assertIn('publication = capture_owned_publication(docker,', source)
        self.assertNotIn('worker-publication.sql', source)

    def test_cleanup_timeout_does_not_mask_the_original_failure(self):
        reader = s.PsqlSnapshot({}, Path('.'))
        reader.errors, reader.transcript = io.BytesIO(), io.BytesIO()
        reader.process = Mock()
        reader.process.poll.side_effect = [None, None, 1]
        reader.process.wait.side_effect = s.subprocess.TimeoutExpired('fixture', 5)
        reader.__exit__(ValueError, ValueError('original'), None)
        self.assertFalse(reader.close_state['rollbackClientExitZero'])
        self.assertFalse(reader.close_state['serverTransactionClosureIndependentlyObserved'])

    def test_uuid_and_page_injection_cannot_enter_static_queries(self):
        q = s.templates()
        for revision in ("x'; DROP TABLE documents;--", 'AAAAAAAA-1111-4111-8111-111111111111'):
            with self.assertRaises(ValueError): s.query_text(q, 'sourceAnchors', revision)
        with self.assertRaises(ValueError): s.query_text(q, 'nativeRawPages_payload', '11111111-1111-4111-8111-111111111111', 0)


if __name__ == '__main__': unittest.main()
