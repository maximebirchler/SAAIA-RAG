"""Causal selection checks. Synthetic protocol; real capture RED is separate."""
import copy
import hashlib
import unittest

from .test_structured_fact_callers import structured_capture
from .test_worker_verifier import search_capture
from canonical_search_checks import choose_current_fact_hit


def values():
    captured, case = structured_capture()
    row = captured[2]['documents'][0]
    response = next(iter(search_capture(captured).values()))['positive']['response']
    return case, row, response


class IngestionSearchReadSelectionTests(unittest.TestCase):
    def test_html_hit_is_selected_for_a_future_real_targeted_get(self):
        case, row, response = values()
        original = copy.deepcopy((case, row, response))
        selected = choose_current_fact_hit(case, row, response)
        self.assertIs(response['items'][0], selected)
        self.assertEqual(original, (case, row, response))

    def test_foreign_identity_changed_bytes_or_page_never_authorize_a_read(self):
        for key, value in [('docId', 'foreign'), ('revisionId', 'foreign'), ('sourceHash', '0'*64),
                           ('chunkId', 'foreign'), ('pageStart', 2), ('text', 'ALPHA -5 °C Ne pas ouvrir')]:
            case, row, response = values()
            response['items'][0][key] = value
            with self.subTest(key=key): self.assertIsNone(choose_current_fact_hit(case, row, response))

    def test_map_checksum_or_bundle_identity_error_is_a_refusal_not_a_raw_fallback(self):
        for mutation in ('map', 'missing_map', 'chunk_checksum', 'bundle_checksum', 'revision'):
            case, row, response = values()
            if mutation == 'map': row['chunks'][0]['metadata']['canonicalTableCellMap']['cells'][0]['literalSha256'] = '0'*64
            elif mutation == 'missing_map': row['chunks'][0]['metadata'].pop('canonicalTableCellMap')
            elif mutation == 'chunk_checksum': row['chunks'][0]['checksumSha256'] = '0'*64
            elif mutation == 'bundle_checksum': row['binaryArtifacts'][0]['contentHash'] = '0'*64
            else: row['revisionId'] = '33333333-3333-4333-8333-333333333333'
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                choose_current_fact_hit(case, row, response)

    def test_expected_text_in_metadata_or_markup_attribute_is_never_a_fact(self):
        for attribute in (False, True):
            case, row, response = values()
            fact = 'fact_only_in_metadata'
            case['expectations']['criticalFacts'][0]['text'] = response['query'] = fact
            row['chunks'][0]['metadata']['literalSearchText'] = fact
            if attribute:
                chunk = row['chunks'][0]
                chunk['text'] = chunk['text'].replace('<table ', '<table data-note="' + fact + '" ', 1)
                chunk['checksumSha256'] = hashlib.sha256(chunk['text'].encode()).hexdigest()
                response['items'][0]['text'] = chunk['text']
            with self.subTest(attribute=attribute): self.assertIsNone(choose_current_fact_hit(case, row, response))

    def test_overlap_ledger_retains_no_raw_fact_selection(self):
        case, row, response = values()
        chunk = row['chunks'][0]
        chunk['text'] = '<canonical-table-observations>' + response['query'] + '</canonical-table-observations>'
        chunk['checksumSha256'] = hashlib.sha256(chunk['text'].encode()).hexdigest()
        chunk['metadata'].pop('canonicalTableCellMap')
        response['items'][0]['text'] = chunk['text']
        self.assertIsNone(choose_current_fact_hit(case, row, response))

    def test_postgresql_base64_line_wrapping_preserves_same_checksummed_bundle(self):
        case, row, response = values()
        artifact = row['binaryArtifacts'][0]
        payload = artifact['payloadBase64']
        artifact['payloadBase64'] = '\r\n'.join(payload[i:i+76] for i in range(0, len(payload), 76))
        self.assertIs(response['items'][0], choose_current_fact_hit(case, row, response))

    def test_arbitrary_base64_characters_are_not_ignored_as_line_wrapping(self):
        for invalid in ('#', '\t', ' '):
            case, row, response = values()
            row['binaryArtifacts'][0]['payloadBase64'] = invalid + row['binaryArtifacts'][0]['payloadBase64']
            with self.subTest(invalid=repr(invalid)), self.assertRaises(ValueError):
                choose_current_fact_hit(case, row, response)


if __name__ == '__main__': unittest.main()
