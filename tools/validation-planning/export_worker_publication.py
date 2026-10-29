"""Complete SQL publication readback, with bounded rows/blobs and one READ ONLY snapshot.

Execution is opt-in and PG-only. No OCR, API, SQL write, container stop or model.
The output preserves worker-publication.sql's JSON data contract, not its old
global PostgreSQL aggregate or the old runner's execution-attestation fields.
"""
from __future__ import annotations

import argparse
import base64
from datetime import datetime, timezone
from decimal import Decimal
import hashlib
import json
import os
from pathlib import Path
import re
import select
import shutil
import subprocess
import sys
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'validation-ingestion'))
from linux_owned_runtime import OwnedDocker, assert_owned_path, validate_owner

CHUNK_BYTES = 57 * 1024
MAX_RECORD_BYTES = 16 * 1024**2
MAX_TOTAL_BYTES = 2 * 1024**3
GLOBAL_ARRAYS = ('retrievalChunks', 'units', 'sections', 'contentCards')
DOCUMENT_ARRAYS = ('sourceAnchors', 'nativeRawPages', 'profiles', 'processingRuns')
DOCUMENT_FIELDS = ('canonicalArtifact', 'sourceAnchors', 'nativeRawCatalog', 'nativeRawPages', 'profiles', 'processingRuns')
NATIVE_COLUMNS = {
    'document_revision_native_raw_catalog': frozenset(('tenant_id doc_id revision_id source_hash generation_id projection_version '
        'processing_run_id canonical_bundle_hash manifest_hash mode reason page_count content_hash payload created_at').split()),
    'document_revision_native_raw_pages': frozenset(('tenant_id doc_id revision_id source_hash generation_id page_number '
        'availability reason block_count content_hash compression byte_size stored_size payload').split())}


def require(value, reason):
    if not value:
        raise ValueError(reason)


def pin(path):
    with Path(path).open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    return {'bytes': Path(path).stat().st_size, 'sha256': digest}


def parsed(raw):
    def pairs(items):
        value = {}
        for key, item in items:
            require(key not in value, 'duplicate_json_key')
            value[key] = item
        return value
    return json.loads(raw.decode('utf-8', 'strict'), parse_float=Decimal, object_pairs_hook=pairs,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError('nonfinite_json')))


def templates():
    result = {}
    for part in Path(__file__).with_name('worker_publication_stream.sql').read_text(encoding='utf-8').split('--@ ')[1:]:
        name, sql = part.split('\n', 1)
        sql = '\n'.join(line for line in sql.splitlines() if not line.lstrip().startswith('--'))
        require(name not in result and sql.strip().startswith('SELECT '), 'static_select_template_required')
        result[name] = sql.strip()
    require(set(result) == {'header', 'counts', 'cell_preflight', 'native_schema', 'documents', 'document_counts', *DOCUMENT_FIELDS,
                           *GLOBAL_ARRAYS, 'canonicalArtifact_payload', 'nativeRawCatalog_payload', 'nativeRawPages_payload'},
            'complete_static_query_inventory_required')
    return result


def query_text(queries, name, revision=None, page=None):
    sql = queries[name]
    if revision is not None:
        require(str(uuid.UUID(revision)) == revision, 'canonical_revision_uuid_required')
        sql = sql.replace('{{revision}}', revision)
    if page is not None:
        require(type(page) is int and page > 0, 'positive_physical_page_required')
        sql = sql.replace('{{page}}', str(page))
    require('{{' not in sql and len(sql.encode('utf-8')) < 65536, 'complete_bounded_static_query_required')
    return sql


class PsqlSnapshot:
    """One psql process, one transaction. Only its own client may be terminated."""
    def __init__(self, owner, output, maximum_record=MAX_RECORD_BYTES, maximum_total=MAX_TOTAL_BYTES):
        self.owner, self.output = owner, output
        self.maximum_record, self.maximum_total = maximum_record, maximum_total
        self.buffer, self.total, self.peak_record, self.ordinal = bytearray(), 0, 0, 0
        self.process = None

    def __enter__(self):
        pg = next(row for row in self.owner['containers'] if row['role'] == 'postgres')
        command = ['docker', '--host', 'unix:///var/run/docker.sock', 'exec', '-i',
                   '-e', 'PGAPPNAME=saaia_worker_publication_stream', pg['id'], 'psql',
                   '-X', '-A', '-t', '-q', '-w', '-v', 'ON_ERROR_STOP=1', '-v', 'FETCH_COUNT=1', '-P', 'pager=off',
                   '--username', self.owner['databaseUser'], '--dbname', self.owner['database']]
        self.errors = self.transcript = None
        try:
            self.errors = (self.output / 'psql-stderr.private.log').open('xb')
            self.transcript = (self.output / 'queries.private.sql').open('xb')
            self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.errors)
            rows = list(self.query("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\n"
                "SET LOCAL client_encoding='UTF8'; SET LOCAL timezone='UTC'; SET LOCAL datestyle='ISO, YMD';\n"
                "SET LOCAL intervalstyle='postgres'; SET LOCAL bytea_output='hex'; SET LOCAL extra_float_digits=3;\n"
                "SET LOCAL work_mem='8MB'; SET LOCAL max_parallel_workers_per_gather=0; SET LOCAL jit=off;\n"
                "SET LOCAL statement_timeout='300s';\n"
                "SELECT jsonb_build_object('readOnly',current_setting('transaction_read_only'),"
                "'isolation',current_setting('transaction_isolation'),'snapshot',pg_current_snapshot()::text,"
                "'applicationName',current_setting('application_name'),'backendPid',pg_backend_pid(),"
                "'serverMajor',current_setting('server_version_num')::int/10000)::text;"))
            require(len(rows) == 1, 'one_snapshot_identity_required')
            self.identity = parsed(rows[0])
            require(self.identity.get('readOnly') == 'on' and self.identity.get('isolation') == 'repeatable read'
                    and self.identity.get('serverMajor') == 16 and self.identity.get('snapshot')
                    and self.identity.get('applicationName') == 'saaia_worker_publication_stream'
                    and type(self.identity.get('backendPid')) is int and self.identity['backendPid'] > 0,
                    'actual_read_only_snapshot_required')
            return self
        except BaseException:
            self.__exit__(*sys.exc_info())
            raise

    def _line(self, deadline):
        while True:
            newline = self.buffer.find(b'\n')
            if newline >= 0:
                require(newline <= self.maximum_record, 'publication_record_size_limit')
                raw = bytes(self.buffer[:newline]); del self.buffer[:newline + 1]
                self.total += len(raw) + 1
                self.peak_record = max(self.peak_record, len(raw))
                require(self.total <= self.maximum_total, 'publication_total_read_size_limit')
                return raw
            require(len(self.buffer) <= self.maximum_record, 'publication_record_size_limit')
            remaining = deadline - time.monotonic()
            require(remaining > 0 and select.select([self.process.stdout], [], [], remaining)[0], 'publication_query_timeout')
            block = os.read(self.process.stdout.fileno(), 65536)
            require(block, 'publication_query_incomplete_check_private_stderr')
            self.buffer.extend(block)

    def query(self, sql):
        self.ordinal += 1
        marker = ('__saaia_publication_end_' + str(self.ordinal) + '__').encode('ascii')
        command = (sql.rstrip(';\n') + ";\nSELECT '" + marker.decode('ascii') + "';\n").encode('utf-8')
        require(len(command) < 65536, 'bounded_statement_required')
        self.transcript.write(command); self.transcript.flush()
        self.process.stdin.write(command); self.process.stdin.flush()
        deadline = time.monotonic() + 330
        while True:
            raw = self._line(deadline)
            if raw == marker:
                return
            require(raw, 'unexpected_empty_psql_row')
            yield raw

    def __exit__(self, error_type, *unused):
        clean = False
        cleanup_error = None
        try:
            if self.process is not None and self.process.poll() is None:
                self.process.stdin.write(b'ROLLBACK;\n'); self.process.stdin.close()
                try:
                    self.process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    self.process.terminate()
                    try:
                        self.process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        self.process.kill(); self.process.wait(timeout=5)
            clean = self.process is not None and self.process.returncode == 0
        except (BrokenPipeError, OSError, subprocess.TimeoutExpired) as error:
            cleanup_error = type(error).__name__
            if self.process is not None and self.process.poll() is None:
                try:
                    self.process.kill(); self.process.wait(timeout=5)
                except (OSError, subprocess.TimeoutExpired): pass
        finally:
            if self.process is not None:
                self.process.stdout.close()
                if not self.process.stdin.closed: self.process.stdin.close()
            if self.errors is not None: self.errors.close()
            if self.transcript is not None: self.transcript.close()
            self.close_state = {'clientReaped': self.process is None or self.process.poll() is not None,
                'clientExitCode': None if self.process is None else self.process.returncode,
                'rollbackClientExitZero': clean, 'cleanupErrorType': cleanup_error,
                'serverTransactionClosureIndependentlyObserved': False}
        if error_type is None:
            require(clean and cleanup_error is None, 'read_only_snapshot_did_not_close_cleanly')


def one(session, sql):
    rows = iter(session.query(sql))
    first = next(rows, None)
    require(first is not None and next(rows, None) is None, 'one_complete_record_required')
    return first


def spool(session, sql, path):
    count = 0
    with path.open('xb') as target:
        for raw in session.query(sql):
            target.write(raw + b'\n'); count += 1
    return count


def rows(path):
    with path.open('rb') as source:
        while raw := source.readline(MAX_RECORD_BYTES + 2):
            require(len(raw) <= MAX_RECORD_BYTES + 1 and raw.endswith(b'\n'), 'closed_spool_record_required')
            yield raw[:-1]


def json_row(raw):
    require(isinstance(parsed(raw), dict) and raw.startswith(b'{') and raw.endswith(b'}'), 'whole_json_object_required')
    return raw


def append_array(target, records, expected):
    target.write(b'['); count = 0
    for raw in records:
        if count: target.write(b',')
        target.write(json_row(raw)); count += 1
        require(count <= expected, 'publication_collection_count_exceeded')
    require(count == expected, 'publication_collection_incomplete')
    target.write(b']')


def append_blob(target, metadata, payload_bytes, encoded_segments):
    """Copy metadata lexemes and JSON-string slices; never parse binary JSON content."""
    metadata = json_row(metadata)
    require('payloadBase64' not in parsed(metadata), 'binary_metadata_must_exclude_payload_field')
    require(type(payload_bytes) is int and payload_bytes >= -1, 'stored_payload_length_invalid')
    target.write(metadata[:-1] + b',"payloadBase64":')
    received, pieces, digest = 0, 0, hashlib.sha256()
    if payload_bytes < 0:
        require(next(iter(encoded_segments), None) is None, 'null_payload_has_segments')
        target.write(b'null}')
        return {'storedBytes': None, 'sha256': None, 'segments': 0}
    target.write(b'"')
    for raw in encoded_segments:
        text = parsed(raw)
        require(isinstance(text, str) and raw.startswith(b'"') and raw.endswith(b'"'), 'encoded_payload_json_string_required')
        require(len(text) <= 80000 and re.fullmatch(r'[A-Za-z0-9+/=\n]*', text), 'bounded_base64_slice_required')
        data = base64.b64decode(text.replace('\n', ''), validate=True)
        expected = min(CHUNK_BYTES, payload_bytes - received)
        require(expected > 0 and len(data) == expected, 'binary_slice_missing_duplicate_or_wrong_size')
        # PostgreSQL encode(bytea,'base64') inserts LF after every 57 input
        # bytes, including the last line when its input is exactly complete.
        # A padded 56-byte final line is also 76 characters, but has no LF.
        trailing_lf = len(data) % 57 == 0
        require(text.endswith('\n') is trailing_lf, 'base64_slice_boundary_changed')
        canonical = base64.b64encode(data).decode('ascii')
        canonical = '\n'.join(canonical[i:i + 76] for i in range(0, len(canonical), 76))
        if trailing_lf: canonical += '\n'
        require(text == canonical, 'base64_wrapping_changed')
        digest.update(data); received += len(data); pieces += 1
        target.write(raw[1:-1])
    require(received == payload_bytes, 'stored_payload_incomplete')
    target.write(b'"}')
    return {'storedBytes': received, 'sha256': digest.hexdigest(), 'segments': pieces}


def capture(session, output, queries):
    """Bounded by one record and one 58,368-byte binary slice, not corpus size."""
    preflight = parsed(one(session, query_text(queries, 'cell_preflight')))
    require(type(preflight.get('maximumTextBytes')) is int and preflight['maximumTextBytes'] <= 1024**2
            and type(preflight.get('maximumJsonbStoredBytes')) is int and preflight['maximumJsonbStoredBytes'] <= 2 * 1024**2,
            'oversize_source_cell_preflight_refused_no_truncation')
    observed_columns = {name: set() for name in NATIVE_COLUMNS}
    for record in session.query(query_text(queries, 'native_schema')):
        table, column = record.decode('ascii').split('\t')
        require(table in observed_columns and column not in observed_columns[table], 'native_schema_inventory_invalid')
        observed_columns[table].add(column)
    require(observed_columns == NATIVE_COLUMNS, 'native_schema_changed_explicit_projection_must_be_reviewed')
    header = json_row(one(session, query_text(queries, 'header')))
    head = parsed(header)
    require(head.get('schemaVersion') == 'saaia-planning-worker-corpus-observation.v1'
            and head.get('advancedJobCount') == head.get('activeJobCount') == 0, 'quiescent_publication_required')
    counts = parsed(one(session, query_text(queries, 'counts')))
    require(set(counts) == {'documents', *GLOBAL_ARRAYS}
            and all(type(value) is int and 0 <= value <= 500000 for value in counts.values())
            and 0 < counts['documents'] <= 64, 'bounded_complete_counts_required')
    require(spool(session, query_text(queries, 'documents'), output / 'documents.jsonl') == counts['documents'], 'document_join_omitted_or_duplicated')
    blob_stats, seen = {'count': 0, 'storedBytes': 0, 'physicalPageCount': 0}, set()
    partial = output / 'publication.private.json.partial'
    with partial.open('xb') as target, (output / 'blobs.jsonl').open('xb') as blob_index:
        def record_blob(value):
            blob_index.write(json.dumps(value).encode('ascii') + b'\n')
            blob_stats['count'] += 1
            blob_stats['storedBytes'] += value['storedBytes'] or 0
        target.write(header[:-1] + b',"documents":[')
        for index, raw in enumerate(rows(output / 'documents.jsonl')):
            doc = parsed(json_row(raw)); revision = doc.get('revisionId')
            require(revision not in seen, 'duplicate_current_revision'); seen.add(revision)
            require(type(doc.get('pageCount')) is int and doc['pageCount'] >= 0, 'physical_page_count_invalid')
            blob_stats['physicalPageCount'] += doc['pageCount']
            if index: target.write(b',')
            target.write(raw[:-1])
            expected = parsed(one(session, query_text(queries, 'document_counts', revision)))
            require(set(expected) == set(DOCUMENT_FIELDS) and all(type(n) is int and 0 <= n <= 500000 for n in expected.values())
                    and expected['canonicalArtifact'] <= 1 and expected['nativeRawCatalog'] <= 1, 'complete_document_collection_counts_required')
            for name in DOCUMENT_FIELDS:
                path = output / (str(index) + '-' + name + '.jsonl')
                require(spool(session, query_text(queries, name, revision), path) == expected[name], 'document_collection_incomplete')
                target.write(b',' + json.dumps(name).encode('ascii') + b':')
                if name in ('canonicalArtifact', 'nativeRawCatalog'):
                    record = next(iter(rows(path)), None)
                    if record is None: target.write(b'null')
                    else:
                        length, metadata = record.split(b'\t', 1)
                        record_blob(dict(revisionId=revision, field=name,
                            **append_blob(target, metadata, int(length), session.query(query_text(queries, name + '_payload', revision)))))
                elif name == 'nativeRawPages':
                    target.write(b'[')
                    prior_page, n = 0, 0
                    for record in rows(path):
                        page, length, metadata = record.split(b'\t', 2); page = int(page)
                        require(page > prior_page and parsed(json_row(metadata)).get('page_number') == page, 'native_page_order_or_identity_changed')
                        if n: target.write(b',')
                        record_blob(dict(revisionId=revision, field=name, physicalPage=page,
                            **append_blob(target, metadata, int(length), session.query(query_text(queries, name + '_payload', revision, page)))))
                        prior_page, n = page, n + 1
                    require(n == expected[name], 'native_pages_incomplete'); target.write(b']')
                else:
                    append_array(target, rows(path), expected[name])
            target.write(b'}')
        target.write(b']')
        for name in GLOBAL_ARRAYS:
            path = output / (name + '.jsonl')
            require(spool(session, query_text(queries, name), path) == counts[name], 'global_collection_incomplete')
            target.write(b',' + json.dumps(name).encode('ascii') + b':')
            append_array(target, rows(path), counts[name])
        require(parsed(one(session, query_text(queries, 'counts'))) == counts, 'snapshot_counts_changed')
        target.write(b'}\n'); target.flush(); os.fsync(target.fileno())
    return counts, blob_stats, preflight, partial


def new_json(path, value):
    with path.open('x', encoding='utf-8', newline='\n') as target:
        json.dump(value, target, indent=2, allow_nan=False); target.write('\n')


def capture_owned_publication(docker, output, owner_sha256, *, recovery_pg_only=False):
    owner = validate_owner(docker.owner)
    root = Path(owner['root']); output = assert_owned_path(output, root)
    require(output.parent.is_dir() and not output.exists(), 'fresh_existing_parent_output_required')
    safe, _ = docker.inspect_all(require_running=not recovery_pg_only)
    if recovery_pg_only:
        require(all(row['running'] is (row['role'] == 'postgres') for row in safe), 'recovery_requires_only_exact_owned_postgres_running')
    require(shutil.disk_usage(root).free >= 2 * MAX_TOTAL_BYTES, 'publication_spool_free_space_required')
    os.umask(0o077); output.mkdir(mode=0o700)
    try:
        with PsqlSnapshot(owner, output) as session:
            counts, blobs, preflight, partial = capture(session, output, templates())
        after, _ = docker.inspect_all(require_running=not recovery_pg_only)
        require(after == safe, 'owned_container_identity_or_state_changed_during_readback')
        publication = output / 'publication.private.json'
        partial.rename(publication)
        files = {p.name: pin(p) for p in sorted(output.iterdir()) if p.is_file()}
        pg = next(row for row in owner['containers'] if row['role'] == 'postgres')
        receipt = {'schemaVersion': 'saaia-worker-publication-stream-capture.v1', 'publicationComplete': True,
            'capturedAtUtc': datetime.now(timezone.utc).isoformat(), 'snapshotReadOnly': True, 'snapshotIsolation': 'repeatable_read',
            'snapshot': session.identity, 'ownerRunId': owner['runId'], 'ownerSha256': owner_sha256,
            'snapshotClosure': session.close_state,
            'postgresContainerId': pg['id'], 'postgresImageId': pg['imageId'],
            'publication': {'path': str(publication), **pin(publication)}, 'counts': counts,
            'blobIndex': {'path': str(output / 'blobs.jsonl'), **pin(output / 'blobs.jsonl'), 'count': blobs['count']},
            'blobsStoredBytes': blobs['storedBytes'], 'sourceCellPreflight': preflight,
            'physicalPageCount': blobs['physicalPageCount'],
            'universalServerJsonbCellMemoryBoundClaimed': False,
            'peakRecordBytes': session.peak_record, 'totalSqlReadBytes': session.total,
            'limits': {'recordBytes': MAX_RECORD_BYTES, 'totalSqlReadBytes': MAX_TOTAL_BYTES, 'binarySliceBytes': CHUNK_BYTES,
                       'postgresWorkMemBytes': 8 * 1024**2, 'parallelWorkers': 0},
            'files': files, 'queryTemplates': pin(Path(__file__).with_name('worker_publication_stream.sql')),
            'sqlWrites': 0, 'apiCalls': 0, 'containersStopped': False, 'reingestionPerformed': False,
            'workerExecutionAttestationSynthesized': False, 'semanticAcceptanceGranted': False, 'productStatus': 'TESTE_NON_APPROUVE'}
        new_json(output / 'capture-receipt.json', receipt)
        return {'publicationComplete': True, 'publicationPath': str(publication), 'counts': counts, 'receipt': str(output / 'capture-receipt.json'),
                'receiptSha256': pin(output / 'capture-receipt.json')['sha256']}
    except BaseException as error:
        try:
            new_json(output / 'capture-failure.json', {'publicationComplete': False, 'errorType': type(error).__name__,
                'reason': str(error) if isinstance(error, ValueError) else 'readback_failed_inspect_private_stderr',
                'partialFilesPreserved': True, 'sqlWrites': 0, 'containersStopped': False})
        except (OSError, ValueError): pass
        raise


def execute(args):
    owner_path = args.owner.resolve(strict=True)
    require(pin(owner_path)['sha256'] == args.expected_owner_sha256, 'owner_bytes_changed')
    owner = validate_owner(json.loads(owner_path.read_bytes()))
    assert_owned_path(args.output, Path(owner['root']))
    if not args.execute:
        return {'admitted': False, 'executeRequired': True, 'runtimeActions': 0}
    return capture_owned_publication(OwnedDocker(owner, execute=True), args.output, args.expected_owner_sha256,
                                     recovery_pg_only=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--owner', type=Path, required=True)
    parser.add_argument('--expected-owner-sha256', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--execute', action='store_true')
    args = parser.parse_args()
    require(re.fullmatch(r'[0-9a-f]{64}', args.expected_owner_sha256), 'actual_owner_sha256_required')
    print(json.dumps(execute(args)))


if __name__ == '__main__':
    main()
