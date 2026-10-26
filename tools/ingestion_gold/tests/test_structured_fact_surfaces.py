"""Synthetic fixtures; real capture RED is preserved separately in artifacts."""
import copy
import hashlib
import html
import unittest

from tools.ingestion_gold.critical_facts import critical_fact_issues
from tools.ingestion_gold.structured_fact_surfaces import (
    validated_chunk_windows, document_source_windows, fact_in_windows, build_retrieval_windows)

DOC, REV, SOURCE = '11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222222', 'a' * 64


def fixture():
    values = [['ALPHA', '-5 °C', 'Ne pas ouvrir'], ['BETA', '1,5 bar', 'Contrôle requis']]
    cells = [{'cellId': f'c{r}{c}', 'rowIndex': r, 'columnIndex': c, 'rowSpan': 1, 'columnSpan': 1,
              'cellRole': 'data', 'text': {'retrieval': value}} for r, row in enumerate(values) for c, value in enumerate(row)]
    document = {'documentId': DOC, 'revisionId': REV, 'source': {'sha256': SOURCE}, 'pages': [
        {'pageNumber': 1, 'blocks': [], 'tables': [{'ordinal': 0, 'rowCount': 2, 'columnCount': 3, 'cells': cells}]}]}
    chunk = {'retrievalChunkId': 'chunk1', 'chunkIndex': 0, 'pageStart': 1, 'pageEnd': 1, 'metadata': {}}
    rebuild(document, chunk)
    return document, chunk


def rebuild(document, chunk):
    table = document['pages'][0]['tables'][0]
    occupied = {(r, c): cell for cell in table['cells']
                for r in range(cell['rowIndex'], cell['rowIndex'] + cell['rowSpan'])
                for c in range(cell['columnIndex'], cell['columnIndex'] + cell['columnSpan'])}
    rows = []
    for r in range(table['rowCount']):
        nodes = []
        for c in range(table['columnCount']):
            cell = occupied.get((r, c))
            if cell is None:
                nodes.append('<td data-canonical-missing="true" />')
                continue
            if cell['rowIndex'] != r or cell['columnIndex'] != c:
                continue
            attr = ''.join(f' {k}="{cell[name]}"' for k, name in [('rowspan', 'rowSpan'), ('colspan', 'columnSpan')]
                           if cell[name] > 1)
            nodes.append('<td' + attr + '>' + html.escape(cell['text']['retrieval']) + '</td>')
        rows.append('<tr>' + ''.join(nodes) + '</tr>')
    chunk['text'] = '<table data-source-table-ordinal="0">' + ''.join(rows) + '</table>'
    chunk['checksumSha256'] = hashlib.sha256(chunk['text'].encode()).hexdigest()
    chunk['metadata'] = {'chunkComposition': 'canonical_table_grid_html_v1',
        'canonicalTableCellIds': [c['cellId'] for c in table['cells']],
        'canonicalTableCellMap': {'schemaVersion': 'canonical_table_cell_map_v1', 'tableOrdinal': 0,
            'totalRows': table['rowCount'], 'totalColumns': table['columnCount'], 'cells': [
                {'cellId': c['cellId'], 'row': c['rowIndex'], 'column': c['columnIndex'],
                 'rowSpan': c['rowSpan'], 'columnSpan': c['columnSpan'], 'role': c['cellRole'],
                 'literalSha256': hashlib.sha256(c['text']['retrieval'].encode()).hexdigest()} for c in table['cells']]}}


def windows(document, chunk, **scope):
    return validated_chunk_windows(chunk, document, document_id=scope.get('document_id', DOC),
                                   revision_id=scope.get('revision_id', REV), source_sha256=scope.get('source_sha256', SOURCE))


class StructuredFactSurfaceTests(unittest.TestCase):
    def test_real_defect_shape_legacy_raw_fails_but_verified_row_retains_exact_fact(self):
        document, chunk = fixture()
        fact = {'id': 'f', 'page': 1, 'text': 'ALPHA -5 °C Ne pas ouvrir'}
        legacy = critical_fact_issues([fact], {1: [fact['text']]}, [chunk])
        self.assertEqual(['critical_retrieval_fact'], [issue['check'] for issue in legacy])
        self.assertTrue(fact_in_windows(fact['text'], windows(document, chunk)))
        self.assertEqual(['c00', 'c01', 'c02'], windows(document, chunk)[0]['cellIds'])

    def test_wrong_coordinates_spans_ids_hash_and_role_refuse_binding(self):
        for mutation in ('row', 'column', 'rowSpan', 'columnSpan', 'cellId', 'literalSha256', 'role', 'float_row', 'bool_row'):
            document, chunk = fixture()
            binding = chunk['metadata']['canonicalTableCellMap']['cells'][0]
            if mutation in ('row', 'column', 'rowSpan', 'columnSpan'): binding[mutation] += 1
            elif mutation == 'float_row': binding['row'] = 0.0
            elif mutation == 'bool_row': binding['row'] = False
            else: binding[mutation] = 'changed'
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): windows(document, chunk)

    def test_doc_revision_source_page_and_chunk_scope_must_match(self):
        for key, value in [('document_id', REV), ('revision_id', DOC), ('source_sha256', 'b' * 64)]:
            with self.subTest(key=key), self.assertRaises(ValueError): windows(*fixture(), **{key: value})
        for key, value in [('pageStart', 2), ('pageEnd', 2), ('docId', REV), ('revisionId', DOC), ('sourceHash', 'b'*64)]:
            document, chunk = fixture(); chunk[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): windows(document, chunk)

    def test_sign_negation_decimal_and_literal_markup_are_not_folded(self):
        document, chunk = fixture()
        found = windows(document, chunk)
        for phrase in ['ALPHA +5 °C Ne pas ouvrir', 'ALPHA -5 °C ouvrir', 'BETA 15 bar', 'ALPHA 5 °C Ne pas ouvrir']:
            self.assertFalse(fact_in_windows(phrase, found), phrase)
        document['pages'][0]['tables'][0]['cells'][1]['text']['retrieval'] = '<limit> & -1,5'
        rebuild(document, chunk)
        self.assertTrue(fact_in_windows('ALPHA <limit> & -1,5 Ne pas ouvrir', windows(document, chunk)))
        self.assertFalse(fact_in_windows('ALPHA limit & -15 Ne pas ouvrir', windows(document, chunk)))

    def test_cross_row_and_cross_table_bags_never_make_a_phrase(self):
        document, chunk = fixture()
        self.assertFalse(fact_in_windows('Ne pas ouvrir BETA', windows(document, chunk)))
        source, errors = document_source_windows(document, document_id=DOC, revision_id=REV, source_sha256=SOURCE)
        self.assertFalse(errors)
        self.assertFalse(fact_in_windows('Ne pas ouvrir BETA', source[1]))

    def test_missing_placeholder_breaks_the_window_instead_of_collapsing_to_space(self):
        document, chunk = fixture()
        document['pages'][0]['tables'][0]['cells'] = [c for c in document['pages'][0]['tables'][0]['cells'] if c['cellId'] != 'c01']
        rebuild(document, chunk)
        self.assertFalse(fact_in_windows('ALPHA Ne pas ouvrir', windows(document, chunk)))
        source, errors = document_source_windows(document, document_id=DOC, revision_id=REV, source_sha256=SOURCE)
        self.assertFalse(errors)
        self.assertFalse(fact_in_windows('ALPHA Ne pas ouvrir', source[1]))

    def test_covered_rowspan_is_not_silently_removed_between_two_other_values(self):
        document, chunk = fixture()
        cells = document['pages'][0]['tables'][0]['cells']
        cells[1]['rowSpan'] = 2
        cells[:] = [c for c in cells if c['cellId'] != 'c11']
        rebuild(document, chunk)
        self.assertFalse(fact_in_windows('BETA Contrôle requis', windows(document, chunk)))

    def test_metadata_literal_text_is_never_fact_evidence(self):
        document, chunk = fixture()
        chunk['metadata']['literalSearchText'] = 'The unavailable expected answer'
        self.assertFalse(fact_in_windows('unavailable expected answer', windows(document, chunk)))

    def test_bad_grid_or_missing_map_cannot_fall_back_to_raw_attributes(self):
        document, chunk = fixture()
        chunk['metadata'].pop('canonicalTableCellMap')
        with self.assertRaises(ValueError): windows(document, chunk)
        chunk['metadata'] = {}
        with self.assertRaises(ValueError): windows(document, chunk)

    def test_caption_is_a_separate_exact_source_bound_window(self):
        document, chunk = fixture()
        document['pages'][0]['tables'][0]['captionBlockIds'] = ['caption']
        document['pages'][0]['blocks'] = [{'blockId': 'caption', 'readingOrder': 0, 'ordinal': 0,
                                          'text': {'retrieval': 'Observed caption'}}]
        chunk['metadata']['canonicalContextBlockIds'] = ['caption']
        chunk['text'] = 'Observed caption\n\n' + chunk['text']
        chunk['checksumSha256'] = hashlib.sha256(chunk['text'].encode()).hexdigest()
        found = windows(document, chunk)
        self.assertTrue(fact_in_windows('Observed caption', found))
        self.assertFalse(fact_in_windows('Observed caption ALPHA', found))
        chunk['metadata']['canonicalContextBlockIds'] = []
        with self.assertRaises(ValueError): windows(document, chunk)

    def test_xml_text_outside_cells_and_chunk_checksum_changes_are_rejected(self):
        for changed in ['checksum', 'outside']:
            document, chunk = fixture()
            if changed == 'checksum': chunk['checksumSha256'] = '0' * 64
            else:
                chunk['text'] = chunk['text'].replace('<tr>', '<tr>invented', 1)
                chunk['checksumSha256'] = hashlib.sha256(chunk['text'].encode()).hexdigest()
            with self.subTest(changed=changed), self.assertRaises(ValueError): windows(document, chunk)

    def test_failing_grid_is_reported_without_mutating_inputs_or_making_successful_windows(self):
        document, chunk = fixture(); original = copy.deepcopy((document, chunk))
        actual, errors = build_retrieval_windows(document, [chunk], document_id=DOC, revision_id=REV, source_sha256=SOURCE)
        self.assertFalse(errors)
        self.assertEqual(original, (document, chunk))
        chunk['metadata']['canonicalTableCellMap']['cells'][0]['literalSha256'] = '0'*64
        actual, errors = build_retrieval_windows(document, [chunk], document_id=DOC, revision_id=REV, source_sha256=SOURCE)
        self.assertEqual([], actual['chunk1'])
        self.assertTrue(errors)

    def test_explicit_overlap_ledger_is_unavailable_not_a_regular_grid_or_raw_evidence(self):
        document, chunk = fixture()
        chunk['text'] = '<canonical-table-observations>ALPHA -5 °C Ne pas ouvrir</canonical-table-observations>'
        chunk['checksumSha256'] = hashlib.sha256(chunk['text'].encode()).hexdigest()
        chunk['metadata'].pop('canonicalTableCellMap')
        actual, errors = build_retrieval_windows(document, [chunk], document_id=DOC, revision_id=REV, source_sha256=SOURCE)
        self.assertEqual([], actual['chunk1'])
        self.assertEqual('UNAVAILABLE', errors[0]['state'])
        self.assertFalse(fact_in_windows('ALPHA -5 °C Ne pas ouvrir', actual['chunk1']))

    def test_overlapping_source_cells_supply_no_invented_row_relation(self):
        document, _ = fixture()
        cells = document['pages'][0]['tables'][0]['cells']
        cells[1]['columnIndex'] = 0
        actual, errors = document_source_windows(document, document_id=DOC, revision_id=REV, source_sha256=SOURCE)
        self.assertEqual('UNAVAILABLE', errors[0]['state'])
        self.assertFalse(fact_in_windows('ALPHA -5 °C Ne pas ouvrir', actual[1]))


if __name__ == '__main__': unittest.main()
