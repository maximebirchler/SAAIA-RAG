"""A mixed execution preserves each original contract and renderer provenance."""
import copy
import hashlib
import json
import unittest

from tools.ingestion_gold.contracts import validate_manifest
from .test_real_source_contract import real_manifest


def case_sha(case):
    return hashlib.sha256(json.dumps(case, sort_keys=True, ensure_ascii=False, allow_nan=False,
                                     separators=(',', ':')).encode()).hexdigest()


def mixed_manifest():
    gold = {'schemaVersion': 'ingestion_acceptance_gold_v2', 'rendererPins': {
        'generatorVersion': 'fixed-test', 'pythonVersion': '3.test', 'reportlabVersion': 'test',
        'pillowVersion': 'test', 'fontSha256': 'b' * 64,
        'generatorSources': {name: 'c' * 64 for name in ('fixtures.py', 'fixtures_fr.py', 'renderer.py')}},
        'cases': [{'caseId': 'synthetic-source', 'generator': 'synthetic-fixture', 'sourceSha256': 'd' * 64,
            'expectations': {'pageCount': 1, 'criticalFacts': [{'id': 'limit', 'page': 1, 'text': 'Do not exceed -5 °C'}]},
            'canonicalExpectations': {'requireProductionDefaults': True}}]}
    origins, cases, bindings = [], [], {}
    for origin_id, provenance, original in [('gold', 'synthetic_renderer', gold),
                                             ('real', 'source_pdf_review', real_manifest())]:
        text = json.dumps(original, indent=2) + '\n'
        origins.append({'originId': origin_id, 'provenance': provenance, 'schemaVersion': original['schemaVersion'],
                        'manifestSha256': hashlib.sha256(text.encode()).hexdigest(), 'manifestUtf8': text})
        for case in original['cases']:
            cases.append(copy.deepcopy(case))
            bindings[case['caseId']] = {'originId': origin_id, 'originCaseId': case['caseId'],
                                         'originManifestSha256': hashlib.sha256(text.encode()).hexdigest(),
                                         'originCaseSha256': case_sha(case)}
    return {'schemaVersion': 'ingestion_mixed_acceptance_v1', 'cases': cases,
            'originManifests': origins, 'caseOrigins': bindings}


class MixedSourceContractTests(unittest.TestCase):
    def test_mixed_cases_keep_gold_generator_and_real_review_with_original_pins(self):
        manifest = mixed_manifest()
        before = copy.deepcopy(manifest)
        self.assertEqual(manifest['cases'], validate_manifest(manifest))
        self.assertEqual(before, manifest)
        self.assertIn('generator', manifest['cases'][0])
        self.assertIn('sourceAnnotations', manifest['cases'][1])

    def test_missing_unknown_duplicate_or_mislabeled_origins_are_refused(self):
        for mutation in ('missing', 'unknown', 'duplicate', 'provenance', 'schema', 'extra_binding'):
            manifest = mixed_manifest()
            if mutation == 'missing':
                del manifest['originManifests']
            elif mutation == 'unknown':
                manifest['caseOrigins']['synthetic-source']['originId'] = 'absent'
            elif mutation == 'duplicate':
                manifest['originManifests'].append(copy.deepcopy(manifest['originManifests'][0]))
            elif mutation == 'provenance':
                manifest['originManifests'][0]['provenance'] = 'source_pdf_review'
            elif mutation == 'schema':
                manifest['originManifests'][0]['schemaVersion'] = 'ingestion_real_acceptance_v1'
            else:
                manifest['caseOrigins']['absent'] = copy.deepcopy(manifest['caseOrigins']['synthetic-source'])
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                validate_manifest(manifest)

    def test_original_hash_and_case_source_fact_generator_manipulations_are_refused(self):
        for mutation in ('original_bytes', 'hash', 'source', 'fact', 'generator', 'case_hash', 'case_origin', 'case_missing'):
            manifest = mixed_manifest()
            if mutation == 'original_bytes':
                manifest['originManifests'][0]['manifestUtf8'] += ' '
            elif mutation == 'hash':
                manifest['originManifests'][0]['manifestSha256'] = 'a' * 64
            elif mutation == 'source':
                manifest['cases'][0]['sourceSha256'] = 'a' * 64
            elif mutation == 'fact':
                manifest['cases'][0]['expectations']['criticalFacts'][0]['text'] = 'Exceed +5 °C'
            elif mutation == 'generator':
                del manifest['cases'][0]['generator']
            elif mutation == 'case_hash':
                manifest['caseOrigins']['synthetic-source']['originCaseSha256'] = 'a' * 64
            elif mutation == 'case_origin':
                manifest['caseOrigins']['synthetic-source']['originCaseId'] = 'real-source'
            else:
                manifest['cases'].pop()
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                validate_manifest(manifest)

    def test_rehashed_invalid_original_still_uses_strict_gold_and_real_validation(self):
        for mutation in ('pins_missing', 'pin_bad_hash', 'facts_missing', 'real_generator', 'real_review'):
            manifest = mixed_manifest()
            index = 1 if mutation.startswith('real_') else 0
            origin = manifest['originManifests'][index]
            original = json.loads(origin['manifestUtf8'])
            if mutation == 'pins_missing':
                del original['rendererPins']
            elif mutation == 'pin_bad_hash':
                original['rendererPins']['fontSha256'] = 'not-a-hash'
            elif mutation == 'facts_missing':
                original['cases'][0]['expectations']['criticalFacts'] = []
            elif mutation == 'real_generator':
                original['cases'][0]['generator'] = 'synthetic'
            else:
                del original['cases'][0]['sourceAnnotations']
            origin['manifestUtf8'] = json.dumps(original)
            origin['manifestSha256'] = hashlib.sha256(origin['manifestUtf8'].encode()).hexdigest()
            for binding in manifest['caseOrigins'].values():
                if binding['originId'] == origin['originId']:
                    binding['originManifestSha256'] = origin['manifestSha256']
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                validate_manifest(manifest)

    def test_rehashed_renderer_change_cannot_bypass_original_manifest_case_binding(self):
        manifest = mixed_manifest()
        origin = manifest['originManifests'][0]
        original = json.loads(origin['manifestUtf8'])
        original['rendererPins']['fontSha256'] = 'e' * 64
        origin['manifestUtf8'] = json.dumps(original)
        origin['manifestSha256'] = hashlib.sha256(origin['manifestUtf8'].encode()).hexdigest()
        with self.assertRaisesRegex(ValueError, 'manifest binding changed'):
            validate_manifest(manifest)

    def test_malformed_origin_types_and_ambiguous_json_are_refused(self):
        for mutation in ('schema_type', 'origin_type', 'duplicate_json', 'nonfinite_json'):
            manifest = mixed_manifest()
            origin = manifest['originManifests'][0]
            if mutation == 'schema_type':
                origin['schemaVersion'] = []
            elif mutation == 'origin_type':
                manifest['caseOrigins']['synthetic-source']['originId'] = []
            else:
                if mutation == 'duplicate_json':
                    origin['manifestUtf8'] = '{"schemaVersion":"one","schemaVersion":"two"}'
                else:
                    origin['manifestUtf8'] = '{"schemaVersion":NaN}'
                origin['manifestSha256'] = hashlib.sha256(origin['manifestUtf8'].encode()).hexdigest()
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                validate_manifest(manifest)


if __name__ == '__main__':
    unittest.main()
