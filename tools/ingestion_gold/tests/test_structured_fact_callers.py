"""Synthetic integration captures; no SQL, API, worker or model is executed."""
import base64
import copy
import gzip
import hashlib
import json
import unittest

from .test_structured_fact_surfaces import fixture
from .test_worker_verifier import captured, search_capture, MODULE as VERIFIER
from .test_linux_retrieval_facts import CHECK


def structured_capture():
    values = list(captured())
    manifest, selected, observation, api, _ = values
    case = next(c for c in manifest['cases'] if c['caseId'] == selected[0])
    case['expectations']['criticalFacts'] = [{'id': 'row-fact', 'page': 1, 'text': 'ALPHA -5 °C Ne pas ouvrir'}]
    row = observation['documents'][0]
    document, chunk = fixture()
    document.update(documentId=row['docId'], revisionId=row['revisionId'])
    document['source']['sha256'] = case['sourceSha256']
    chunk['retrievalChunkId'] = row['chunks'][0]['retrievalChunkId']
    row['chunks'] = [chunk]
    bundle = {'schemaVersion': 'canonical_ingestion_bundle_v1', 'document': document,
              'sourceAnchors': [{'anchorId': 'a'}]}
    raw = json.dumps(bundle, ensure_ascii=False).encode('utf-8')
    compressed = gzip.compress(raw, mtime=0)
    row['binaryArtifacts'][0].update(payloadBase64=base64.b64encode(compressed).decode(), byteSize=len(raw),
        storedSize=len(compressed), contentHash=hashlib.sha256(raw).hexdigest())
    api[row['docPath']][0]['response']['items'] = [{'chunkId': chunk['retrievalChunkId'], 'text': chunk['text']}]
    return values, case


class StructuredFactCallerTests(unittest.TestCase):
    def test_verifier_keeps_legacy_false_and_proves_new_sql_api_search_windows(self):
        values, _ = structured_capture()
        original = copy.deepcopy(values)
        proof = VERIFIER.verify(*values, searches=search_capture(values), require_search=True)
        self.assertTrue(proof['passed'], proof)
        result = proof['results'][0]
        self.assertFalse(result['criticalFactsLegacyRawPassed'])
        self.assertTrue(result['criticalFactsSourceBoundPassed'])
        self.assertFalse(result['readApiFactsLegacyRawPassed'])
        self.assertTrue(result['readApiFactsPassed'])
        self.assertFalse(result['canonicalSearch']['positiveFactFoundLegacyRawText'])
        self.assertTrue(result['canonicalSearch']['positiveFactFound'])
        self.assertEqual(original, values)

    def test_api_page_read_requires_same_sql_chunk_id_bytes_page_and_source(self):
        for mutation in ('chunkId', 'text', 'source', 'page'):
            values, _ = structured_capture()
            captured_page = next(iter(values[3].values()))[0]
            item = captured_page['response']['items'][0]
            if mutation == 'chunkId': item['chunkId'] = 'foreign'
            elif mutation == 'text': item['text'] = item['text'].replace('Ne pas ouvrir', 'Ouvrir')
            elif mutation == 'source': captured_page['response']['document']['sourceHash'] = '0' * 64
            else: captured_page['physicalPage'] = 2
            proof = VERIFIER.verify(*values, searches=search_capture(values), require_search=True)
            with self.subTest(mutation=mutation):
                self.assertFalse(proof['passed'])
                self.assertFalse(proof['results'][0]['readApiFactsPassed'])

    def test_fact_probe_uses_source_grid_but_never_accepts_stale_or_changed_hit(self):
        values, case = structured_capture()
        row = CHECK.baseline([case], values[2])[case['caseId']]
        capture = next(iter(search_capture(values).values()))
        fact = case['expectations']['criticalFacts'][0]
        result = CHECK.fact_check(case, row, fact, capture['positive']['response'], capture['read'])
        self.assertTrue(result['passed'], result)
        self.assertFalse(result['exactFactFoundLegacyRawText'])
        for mutation in ('revisionId', 'sourceHash', 'docId', 'text', 'read'):
            changed = copy.deepcopy(capture)
            hit = changed['positive']['response']['items'][0]
            if mutation == 'read': changed['read']['response']['items'][0]['text'] += ' changed'
            elif mutation == 'text': hit['text'] = hit['text'].replace('-5 °C', '+5 °C')
            else: hit[mutation] = 'foreign'
            result = CHECK.fact_check(case, row, fact, changed['positive']['response'], changed['read'])
            with self.subTest(mutation=mutation): self.assertFalse(result['passed'])

    def test_invalid_grid_does_not_reenter_raw_search_through_metadata_attributes(self):
        values, case = structured_capture()
        chunk = values[2]['documents'][0]['chunks'][0]
        fact = case['expectations']['criticalFacts'][0]
        chunk['text'] = chunk['text'].replace('<table ', '<table expected="' + fact['text'] + '" ', 1)
        chunk['checksumSha256'] = hashlib.sha256(chunk['text'].encode()).hexdigest()
        chunk['metadata'].pop('canonicalTableCellMap')
        for item in next(iter(values[3].values()))[0]['response']['items']: item['text'] = chunk['text']
        proof = VERIFIER.verify(*values, searches=search_capture(values), require_search=True)
        self.assertFalse(proof['passed'])
        self.assertFalse(proof['results'][0]['canonicalSearch']['positiveFactFound'])
        self.assertFalse(proof['results'][0]['criticalFactsSourceBoundPassed'])
        with self.assertRaises(ValueError): CHECK.baseline([case], values[2])


if __name__ == '__main__': unittest.main()
