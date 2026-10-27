import asyncio
from copy import deepcopy
from dataclasses import dataclass, replace
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from saaia_docling.response_repair import repair_response_tables
from saaia_docling.table_region_process import OcrRegionResult
from saaia_docling.table_repair_options import TableRepairOptions
from saaia_docling.table_observation_options import TableObservationWorkOptions, requested_table_observations
from saaia_docling.execution_metadata import conversion_execution_metadata
from test_response_table_repair import response
from test_table_repair import _reference_ocr_lines


@dataclass(frozen=True)
class ObservationOptions(TableRepairOptions):
    alternative_readings_enabled: bool = True
    repair_enabled: bool = True


def complete_response(count=1):
    payload = response(count)
    for table in payload['document']['json_content']['tables']:
        # The reference fixture's final hole is deliberately filled here.
        cells = table['data']['table_cells']
        occupied = {(c['start_row_offset_idx'], c['start_col_offset_idx']) for c in cells}
        for row in range(table['data']['num_rows']):
            for column in range(table['data']['num_cols']):
                if (row, column) not in occupied:
                    cells.append({'text': 'literal source', 'start_row_offset_idx': row,
                        'start_col_offset_idx': column, 'row_span': 1, 'col_span': 1,
                        'bbox': {'l': column * 30, 'r': column * 30 + 20,
                            't': row * 10, 'b': row * 10 + 8, 'coord_origin': 'TOPLEFT'}})
    return payload


class ObservationEngine:
    def __init__(self, *, advances=(), now=None, error=None):
        self.calls, self.advances, self.now, self.error = [], list(advances), now, error

    async def recognize(self, pdf_path, document, table, options, timeout_seconds):
        self.calls.append((table['self_ref'], timeout_seconds))
        if self.advances:
            self.now[0] += self.advances.pop(0)
        if self.error:
            raise self.error
        lines = tuple(_reference_ocr_lines())
        observation = {'engine': 'rapidocr', 'engine_version': '3.9.1',
            'model_id': 'local-observed-model', 'model_sha256': None,
            'model_artifacts': {'det.onnx': 'a' * 64}, 'duration_ms': 2,
            'coordinate_origin': 'TOPLEFT', 'minimum_confidence': .99,
            'runtime_settings': {'providers': ['CPUExecutionProvider'],
                'intra_op_num_threads': 2, 'inter_op_num_threads': 1},
            'lines': [{'text': line.text, 'confidence': line.score,
                'polygon': [list(p) for p in line.polygon]} for line in lines]}
        return OcrRegionResult(lines, 2, '3.9.1', 'local-observed-model', observation)


class TableRegionObservationTests(unittest.IsolatedAsyncioTestCase):
    def test_activation_is_explicit_and_does_not_guess_ambiguous_values(self):
        self.assertFalse(requested_table_observations([]))
        self.assertFalse(requested_table_observations([('saaia_table_observations_enabled', 'false')]))
        self.assertTrue(requested_table_observations([('saaia_table_observations_enabled', 'true')]))
        for values in (['TRUE'], ['1'], ['true', 'false']):
            with self.subTest(values=values), self.assertRaises(ValueError):
                requested_table_observations([('saaia_table_observations_enabled', value) for value in values])

    def test_execution_extension_is_absent_when_off(self):
        kwargs = dict(repair_enabled=True, render_scale=2.5, padding_points=12, minimum_score=.5)
        legacy = conversion_execution_metadata([], **kwargs)
        self.assertNotIn('table_observation_settings', legacy)
        self.assertEqual(legacy, conversion_execution_metadata([], table_observation_settings=None, **kwargs))

    def test_actual_work_options_do_not_leak_new_controls_to_engine_child(self):
        from saaia_docling.table_region_process import build_table_region_request
        document = response()['document']['json_content']
        options = TableObservationWorkOptions.from_table_options(TableRepairOptions(), repair_enabled=False)
        request = build_table_region_request(Path('unopened.pdf'), document, document['tables'][0], options, 15)
        self.assertNotIn('alternative_readings_enabled', request['options'])
        self.assertNotIn('repair_enabled', request['options'])
        self.assertFalse(options.repair_enabled)

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.source = Path(self.directory.name) / 'source.pdf'
        self.source.write_bytes(b'%PDF-owned-synthetic-input-bytes')

    def tearDown(self):
        self.directory.cleanup()

    def envelope(self, payload):
        document = payload['document']['json_content']
        self.assertIn('saaia_table_region_observations', document)
        return document['saaia_table_region_observations']

    async def test_feature_off_preserves_the_exact_legacy_primary_json(self):
        first, second = response(), response()
        await repair_response_tables(self.source, first, ObservationEngine(), clock=lambda: 0)
        await repair_response_tables(self.source, second, ObservationEngine(),
            options=ObservationOptions(alternative_readings_enabled=False), clock=lambda: 0)
        self.assertEqual(json.dumps(first, ensure_ascii=False), json.dumps(second, ensure_ascii=False))
        self.assertNotIn('saaia_table_region_observations', second['document']['json_content'])

    async def test_complete_grid_is_observed_without_rewriting_any_cells(self):
        payload, engine = complete_response(), ObservationEngine()
        original = deepcopy(payload['document']['json_content']['tables'])
        await repair_response_tables(self.source, payload, engine,
            options=ObservationOptions(), clock=lambda: 0)
        self.assertEqual(1, len(engine.calls))
        self.assertEqual(original, payload['document']['json_content']['tables'])
        envelope = self.envelope(payload)
        self.assertEqual('observed', envelope['availability'])
        self.assertEqual(hashlib.sha256(self.source.read_bytes()).hexdigest(), envelope['source_sha256'])
        self.assertEqual(1, len(envelope['observations']))
        raw = envelope['observations'][0]
        self.assertEqual(hashlib.sha256(raw['raw_observation_json'].encode()).hexdigest(), raw['raw_observation_sha256'])
        self.assertEqual(len(_reference_ocr_lines()), len(json.loads(raw['raw_observation_json'])['lines']))
        self.assertNotIn('revision_id', raw)

    async def test_sparse_and_complete_tables_share_one_attempt_budget_with_sparse_priority(self):
        payload = complete_response(2)
        payload['document']['json_content']['tables'][1]['data'] = response()['document']['json_content']['tables'][0]['data']
        engine = ObservationEngine()
        await repair_response_tables(self.source, payload, engine,
            options=ObservationOptions(max_regions=1), clock=lambda: 0)
        self.assertEqual(['#/tables/1'], [c[0] for c in engine.calls])
        envelope = self.envelope(payload)
        self.assertEqual('partial', envelope['availability'])
        self.assertEqual(1, envelope['attempted_region_count'])
        self.assertEqual(1, envelope['completed_observation_count'])
        self.assertEqual(2, len(envelope['attempts']))
        self.assertEqual(1, payload['saaia_table_repair_summary']['attempted_count'])

    async def test_secondary_scheduling_preserves_legacy_sparse_order_when_tails_differ(self):
        payload = response(2)
        payload['document']['json_content']['tables'][1]['prov'][0]['bbox']['b'] = 60
        legacy, secondary = deepcopy(payload), deepcopy(payload)
        legacy_engine, secondary_engine = ObservationEngine(), ObservationEngine()
        await repair_response_tables(self.source, legacy, legacy_engine,
            options=TableRepairOptions(max_regions=1), clock=lambda: 0)
        await repair_response_tables(self.source, secondary, secondary_engine,
            options=ObservationOptions(max_regions=1), clock=lambda: 0)
        self.assertEqual(['#/tables/0'], [call[0] for call in legacy_engine.calls])
        self.assertEqual(legacy_engine.calls, secondary_engine.calls,
            'A secondary inspection priority must not reorder primary sparse repairs.')

    async def test_secondary_activation_keeps_the_same_primary_repair_under_one_region_budget(self):
        payload = response(2)
        payload['document']['json_content']['tables'][1]['prov'][0]['bbox']['b'] = 60
        legacy, secondary = deepcopy(payload), deepcopy(payload)
        await repair_response_tables(self.source, legacy, ObservationEngine(),
            options=TableRepairOptions(max_regions=1), clock=lambda: 0)
        await repair_response_tables(self.source, secondary, ObservationEngine(),
            options=ObservationOptions(max_regions=1), clock=lambda: 0)
        legacy_data = [table['data'] for table in legacy['document']['json_content']['tables']]
        secondary_data = [table['data'] for table in secondary['document']['json_content']['tables']]
        self.assertEqual([9, 7], [len(table['table_cells']) for table in legacy_data])
        self.assertEqual(legacy_data, secondary_data,
            'The feature must preserve which primary table receives the bounded repair.')

    async def test_secondary_complete_tables_still_use_tail_priority_after_sparse_work(self):
        payload, engine = complete_response(2), ObservationEngine()
        payload['document']['json_content']['tables'][1]['prov'][0]['bbox']['b'] = 60
        original = deepcopy(payload['document']['json_content']['tables'])
        await repair_response_tables(self.source, payload, engine,
            options=ObservationOptions(max_regions=1), clock=lambda: 0)
        self.assertEqual(['#/tables/1'], [call[0] for call in engine.calls])
        self.assertEqual(original, payload['document']['json_content']['tables'])
        self.assertEqual(0, payload['saaia_table_repair_summary']['attempted_count'])

    async def test_one_sparse_read_is_reused_by_repair_and_alternative(self):
        payload, engine = response(), ObservationEngine()
        await repair_response_tables(self.source, payload, engine,
            options=ObservationOptions(), clock=lambda: 0)
        self.assertEqual(1, len(engine.calls))
        self.assertEqual('applied', payload['saaia_table_repairs'][0]['status'])
        self.assertEqual(1, len(self.envelope(payload)['observations']))

    async def test_observed_zero_and_unavailable_inventory_are_distinct(self):
        empty, unavailable = response(0), response(0)
        del unavailable['document']['json_content']['tables']
        for payload in (empty, unavailable):
            await repair_response_tables(self.source, payload, ObservationEngine(),
                options=ObservationOptions(), clock=lambda: 0)
        self.assertEqual([], self.envelope(empty)['observations'])
        self.assertEqual('observed', self.envelope(empty)['availability'])
        self.assertIsNone(self.envelope(unavailable)['observations'])
        self.assertIsNone(self.envelope(unavailable)['source_table_count'])
        self.assertEqual('unavailable', self.envelope(unavailable)['availability'])

    async def test_late_result_is_not_admitted_and_has_no_fake_observation(self):
        now = [0.0]
        payload = complete_response()
        engine = ObservationEngine(advances=[61], now=now)
        await repair_response_tables(self.source, payload, engine,
            options=ObservationOptions(), clock=lambda: now[0])
        envelope = self.envelope(payload)
        self.assertEqual([], envelope['observations'])
        self.assertEqual('timeout', envelope['attempts'][0]['status'])
        self.assertEqual('partial', envelope['availability'])

    async def test_hash_deadline_refuses_only_secondary_channel_with_unknown_source_hash(self):
        now = iter((0.0, 61.0))
        payload, engine = complete_response(), ObservationEngine()
        primary = deepcopy(payload['document']['json_content'])
        error = None
        try:
            await repair_response_tables(self.source, payload, engine,
                options=ObservationOptions(), clock=lambda: next(now, 61.0))
        except TimeoutError as caught:
            error = type(caught).__name__
        self.assertIsNone(error, 'Secondary hash deadline must preserve the successful primary conversion.')
        envelope = self.envelope(payload)
        self.assertEqual('unavailable', envelope['availability'])
        self.assertEqual('source_hash_time_budget_exhausted', envelope['reason'])
        self.assertIsNone(envelope['source_sha256'])
        for field in ('source_table_count', 'inspected_table_count', 'uninspected_table_count',
                'attempted_region_count', 'completed_observation_count', 'attempts', 'observations'):
            self.assertIsNone(envelope[field])
        self.assertEqual([], engine.calls)
        current = deepcopy(payload['document']['json_content'])
        del current['saaia_table_region_observations']
        self.assertEqual(primary, current)

    async def test_source_hash_elapsed_time_remains_in_the_one_shared_deadline(self):
        now = [0.0]
        def completed_hash(*args):
            now[0] = 58.0
            return hashlib.sha256(self.source.read_bytes()).hexdigest()
        payload, engine = complete_response(), ObservationEngine()
        with patch('saaia_docling.table_region_observations._source_sha', side_effect=completed_hash):
            await repair_response_tables(self.source, payload, engine,
                options=ObservationOptions(), clock=lambda: now[0])
        self.assertEqual([2.0], [call[1] for call in engine.calls])

    async def test_cancel_during_source_hash_still_propagates_without_mutating_primary(self):
        payload = complete_response()
        original = deepcopy(payload)
        with patch('saaia_docling.table_region_observations._source_sha', side_effect=asyncio.CancelledError()), \
                self.assertRaises(asyncio.CancelledError):
            await repair_response_tables(self.source, payload, ObservationEngine(),
                options=ObservationOptions(), clock=lambda: 0)
        self.assertEqual(original, payload)

    async def test_total_deadline_is_shared_and_second_timeout_uses_only_remaining_time(self):
        now = [0.0]
        payload, engine = complete_response(2), ObservationEngine(advances=[58, 4], now=now)
        await repair_response_tables(self.source, payload, engine,
            options=ObservationOptions(), clock=lambda: now[0])
        self.assertEqual([15, 2], [call[1] for call in engine.calls])
        self.assertEqual(1, len(self.envelope(payload)['observations']))
        self.assertEqual(2, self.envelope(payload)['attempted_region_count'])

    async def test_cancel_propagates_without_an_admitted_payload(self):
        payload = complete_response()
        original = deepcopy(payload)
        with self.assertRaises(asyncio.CancelledError):
            await repair_response_tables(self.source, payload,
                ObservationEngine(error=asyncio.CancelledError()), options=ObservationOptions(), clock=lambda: 0)
        self.assertEqual(original, payload)

    async def test_error_is_explicit_without_private_exception_text_or_observation(self):
        payload = complete_response()
        await repair_response_tables(self.source, payload,
            ObservationEngine(error=RuntimeError('PRIVATE_SENTINEL')),
            options=ObservationOptions(), clock=lambda: 0)
        envelope = self.envelope(payload)
        self.assertEqual('error', envelope['attempts'][0]['status'])
        self.assertEqual([], envelope['observations'])
        self.assertNotIn('PRIVATE_SENTINEL', json.dumps(envelope))

    async def test_eight_attempt_cap_does_not_become_an_observed_empty_catalog(self):
        payload, engine = complete_response(9), ObservationEngine()
        await repair_response_tables(self.source, payload, engine,
            options=ObservationOptions(), clock=lambda: 0)
        self.assertEqual(8, len(engine.calls))
        envelope = self.envelope(payload)
        self.assertEqual(8, len(envelope['observations']))
        self.assertEqual('partial', envelope['availability'])
        self.assertEqual(9, len(envelope['attempts']))

    async def test_duplicate_source_reference_refuses_inventory_before_any_engine(self):
        payload, engine = complete_response(2), ObservationEngine()
        payload['document']['json_content']['tables'][1]['self_ref'] = '#/tables/0'
        await repair_response_tables(self.source, payload, engine,
            options=ObservationOptions(), clock=lambda: 0)
        self.assertEqual([], engine.calls)
        envelope = self.envelope(payload)
        self.assertEqual('unavailable', envelope['availability'])
        self.assertIsNone(envelope['observations'])
        self.assertIsNone(envelope['attempted_region_count'])

    async def test_raw_and_decoded_line_disagreement_never_becomes_an_observation(self):
        class ContradictingEngine(ObservationEngine):
            async def recognize(self, *args):
                result = await super().recognize(*args)
                result.observation['lines'][0]['text'] = 'different literal'
                return result
        payload = complete_response()
        await repair_response_tables(self.source, payload, ContradictingEngine(),
            options=ObservationOptions(), clock=lambda: 0)
        envelope = self.envelope(payload)
        self.assertEqual([], envelope['observations'])
        self.assertEqual('error', envelope['attempts'][0]['status'])

    async def test_post_decode_work_crossing_deadline_is_not_published(self):
        from saaia_docling.table_observation_builder import build_observation
        now = [0.0]
        def late(*args):
            result = build_observation(*args)
            now[0] = 61
            return result
        payload = complete_response()
        with patch('saaia_docling.table_region_observations.build_observation', side_effect=late):
            await repair_response_tables(self.source, payload, ObservationEngine(),
                options=ObservationOptions(), clock=lambda: now[0])
        self.assertEqual([], self.envelope(payload)['observations'])
        self.assertEqual('timeout', self.envelope(payload)['attempts'][0]['status'])

    async def test_oversized_secondary_payload_is_refused_without_failing_primary(self):
        class HugeEngine(ObservationEngine):
            async def recognize(self, *args):
                result = await super().recognize(*args)
                result.observation['private_extra'] = 'x' * 2_000_000
                return result
        payload = complete_response()
        original = deepcopy(payload['document']['json_content']['tables'])
        await repair_response_tables(self.source, payload, HugeEngine(),
            options=ObservationOptions(), clock=lambda: 0)
        self.assertEqual(original, payload['document']['json_content']['tables'])
        envelope = self.envelope(payload)
        self.assertEqual('refused', envelope['attempts'][0]['status'])
        self.assertEqual('table_observation_raw_byte_limit', envelope['attempts'][0]['reason'])
        self.assertEqual([], envelope['observations'])
        self.assertEqual('partial', envelope['availability'])


if __name__ == '__main__':
    unittest.main()
