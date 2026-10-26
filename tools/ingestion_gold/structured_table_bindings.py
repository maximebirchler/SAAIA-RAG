"""Validate persisted bindings, never infer an ID from the position of an ID list."""
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from .structured_literal_storage import canonical_projected_literal

MAX_CELLS = 16_384
HASH = re.compile('[a-f0-9]{64}\\Z')


def require(condition, code):
    if not condition:
        raise ValueError(code)


def digest(text):
    return hashlib.sha256(text.encode('utf-8')).hexdigest()


def obj(value):
    if isinstance(value, str):
        require(len(value) <= 4_000_000, 'metadata_size_bound')
        return json.loads(value)
    return value


def canonical_value(cell):
    value = cell.get('text', {})
    if isinstance(value, str):
        return value
    require(isinstance(value, dict), 'canonical_literal_type')
    require(isinstance(value.get('retrieval'), str), 'canonical_retrieval_literal_missing')
    return value['retrieval']


def row_indices(root, total):
    if root.get('data-fragment') != 'true':
        return list(range(total))
    require(root.get('data-total-rows') == str(total), 'html_total_rows_changed')
    text = root.get('data-source-row-indices', '')
    require(len(text) <= 60_000, 'html_row_index_bound')
    result = []
    for value in text.split(','):
        require(re.fullmatch('[0-9]+(?:-[0-9]+)?', value) is not None, 'html_row_index_invalid')
        bounds = [int(v) for v in value.split('-')]
        start, end = bounds[0], bounds[-1]
        require(0 <= start <= end < total and len(result) + end - start + 1 <= 10_000, 'html_row_index_bound')
        result.extend(range(start, end + 1))
    require(result == sorted(set(result)), 'html_row_index_duplicate')
    return result


def audit_chunk(chunk, document):
    # Caller must first bind document/revision/source through the pinned capture
    # decoder. This helper checks the contents of that already-bound document.
    metadata = obj(chunk.get('metadata')) or {}
    require(isinstance(metadata, dict), 'metadata_not_object')
    binding_map = obj(metadata.get('canonicalTableCellMap'))
    declared = metadata.get('chunkComposition') == 'canonical_table_grid_html_v1' or any(
        metadata.get(k) == 'docling_canonical_table_v1' for k in ['chunkType', 'originalChunkType'])
    if '<canonical-table-observations' in chunk['text']:
        require(binding_map is None, 'overlap_ledger_has_grid_map')
        return {'state': 'EXPLICIT_OVERLAP_LEDGER_NOT_A_REGULAR_GRID', 'cellsChecked': 0}
    if not declared and binding_map is None:
        return {'state': 'NOT_A_STRUCTURED_GRID', 'cellsChecked': 0}
    if binding_map is None:
        return {'state': 'LEGACY_MAP_UNAVAILABLE', 'cellsChecked': 0}
    require(isinstance(binding_map, dict) and binding_map.get('schemaVersion') == 'canonical_table_cell_map_v1',
            'map_schema_invalid')
    start = chunk['text'].find('<table')
    end = chunk['text'].find('</table>', start)
    require(start >= 0 and end >= start and chunk['text'].find('<table', start + 1) < 0, 'html_grid_missing_or_multiple')
    html = chunk['text'][start:end + 8]
    require(len(html) <= 200_000 and '<!' not in html and '<?' not in html, 'html_declaration_or_size_bound')
    root = ET.fromstring(html)
    require(root.tag == 'table' and all(node.tag in {'table', 'tr', 'th', 'td'} for node in root.iter()), 'html_element_invalid')
    require(all(not (node.text or '').strip() for node in root.iter() if node.tag in {'table', 'tr'})
            and all(not (node.tail or '').strip() for node in root.iter()), 'html_noncell_text')
    ordinal, total_rows, total_columns = (binding_map.get(k) for k in ['tableOrdinal', 'totalRows', 'totalColumns'])
    require(all(type(v) is int for v in [ordinal, total_rows, total_columns]) and ordinal >= 0
            and 0 < total_rows <= 10_000 and 0 < total_columns <= 128, 'map_dimensions_invalid')
    require(root.get('data-source-table-ordinal') == str(ordinal), 'html_map_table_ordinal_mismatch')
    require(type(chunk.get('pageStart')) is int and chunk['pageStart'] > 0
            and chunk.get('pageEnd') == chunk['pageStart'], 'single_physical_page_required')
    tables = [table for page in document.get('pages', []) if page.get('pageNumber') == chunk['pageStart']
              for table in page.get('tables', []) if table.get('ordinal') == ordinal]
    require(len(tables) == 1, 'canonical_table_identity_ambiguous')
    table = tables[0]
    require(table.get('rowCount') == total_rows and table.get('columnCount') == total_columns, 'canonical_map_dimensions_mismatch')
    source_cells = table.get('cells', [])
    require(isinstance(source_cells, list) and len(source_cells) <= MAX_CELLS, 'canonical_cell_count_bound')
    originals, by_id, occupied = {}, {}, {}
    for cell in source_cells:
        identity = cell.get('cellId')
        row, column, rs, cs = (cell.get(k) for k in ['rowIndex', 'columnIndex', 'rowSpan', 'columnSpan'])
        require(isinstance(identity, str) and identity and identity not in by_id, 'canonical_cell_id_duplicate_or_missing')
        require(all(type(v) is int for v in [row, column, rs, cs]) and 0 <= row < total_rows
                and 0 <= column < total_columns and 1 <= rs <= total_rows - row and 1 <= cs <= total_columns - column,
                'canonical_cell_coordinates_invalid')
        by_id[identity], originals[(row, column)] = cell, cell
        for r in range(row, row + rs):
            for c in range(column, column + cs):
                require((r, c) not in occupied, 'canonical_grid_overlaps')
                occupied[(r, c)] = cell
    ids = metadata.get('canonicalTableCellIds')
    require(isinstance(ids, list) and ids and len(ids) <= MAX_CELLS and all(isinstance(v, str) for v in ids)
            and len(set(ids)) == len(ids), 'published_cell_id_set_invalid')
    cells = binding_map.get('cells')
    require(isinstance(cells, list) and len(cells) == len(ids), 'map_cell_count_mismatch')
    bindings = {}
    normalization_count = 0
    for binding in cells:
        require(isinstance(binding, dict), 'map_binding_not_object')
        identity = binding.get('cellId')
        require(isinstance(identity, str) and identity in by_id and identity not in bindings, 'map_cell_id_unknown_or_duplicate')
        source = by_id[identity]
        for key, canonical in [('row', 'rowIndex'), ('column', 'columnIndex'), ('rowSpan', 'rowSpan'),
                               ('columnSpan', 'columnSpan'), ('role', 'cellRole')]:
            require(key == 'role' or type(binding.get(key)) is int, 'map_binding_coordinate_type_invalid')
            require(binding.get(key) == source.get(canonical), 'map_canonical_coordinate_span_or_role_mismatch')
        value = canonical_projected_literal(canonical_value(source))
        normalization_count += value != canonical_value(source)
        require(isinstance(binding.get('literalSha256'), str) and HASH.fullmatch(binding['literalSha256']) is not None
                and digest(value) == binding['literalSha256'], 'map_canonical_literal_hash_mismatch')
        bindings[identity] = binding
    require(set(bindings) == set(ids), 'map_published_cell_id_set_mismatch')
    indices = row_indices(root, total_rows)
    nodes = list(root)
    require(len(nodes) == len(indices) and all(node.tag == 'tr' for node in nodes), 'html_row_count_mismatch')
    seen = set()
    for row, node in zip(indices, nodes):
        column = 0
        for cell_node in node:
            while column < total_columns and (row, column) in occupied and occupied[(row, column)]['rowIndex'] < row:
                require(occupied[(row, column)]['rowIndex'] in indices, 'html_rowspan_leading_row_missing')
                column += 1
            require(column < total_columns and cell_node.tag in {'td', 'th'} and not list(cell_node), 'html_cell_shape_invalid')
            value = cell_node.text or ''
            source = originals.get((row, column))
            if cell_node.get('data-canonical-missing') == 'true':
                require(source is None and (row, column) not in occupied and not value
                        and cell_node.tag == 'td' and cell_node.get('rowspan', '1') == '1'
                        and cell_node.get('colspan', '1') == '1', 'html_missing_slot_fabricated')
                column += 1
                continue
            require(source is not None and source['cellId'] in bindings, 'html_cell_binding_missing')
            binding = bindings[source['cellId']]
            require(source['cellId'] not in seen, 'html_cell_repeated')
            require(cell_node.get('rowspan', '1') == str(binding['rowSpan'])
                    and cell_node.get('colspan', '1') == str(binding['columnSpan']), 'html_map_span_mismatch')
            require(cell_node.get('data-cell-role', 'data') == binding['role']
                    and (cell_node.tag == 'th') == (binding['role'] != 'data'), 'html_map_role_mismatch')
            require(digest(value) == binding['literalSha256'], 'html_map_literal_hash_mismatch')
            seen.add(source['cellId'])
            column += binding['columnSpan']
        while column < total_columns and (row, column) in occupied and occupied[(row, column)]['rowIndex'] < row:
            require(occupied[(row, column)]['rowIndex'] in indices, 'html_rowspan_leading_row_missing')
            column += 1
        require(column == total_columns, 'html_row_width_mismatch')
    require(seen == set(ids), 'html_anchored_cells_not_all_rendered')
    return {'state': 'MAP_HTML_CANONICAL_BINDINGS_CONSISTENT', 'tableOrdinal': ordinal,
            'physicalPage': chunk['pageStart'], 'cellsChecked': len(seen), 'rowsChecked': len(indices),
            'canonicalLiteralsMechanicallyNormalized': normalization_count,
            'semanticAccuracyValidated': False, 'sourceFidelityValidated': False}
