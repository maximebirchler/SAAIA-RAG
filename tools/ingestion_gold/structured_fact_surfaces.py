"""Exact fact windows from source-bound published grids; never generic HTML stripping.

A row is one window. Captions are separate windows. No concatenation of data
rows, pages, documents, headers and body can manufacture a phrase. The original
SQL/API strings remain authoritative for identity/checksum checks.
"""
import hashlib
import re
import uuid
import xml.etree.ElementTree as ET

from .critical_facts import exact_text
from .structured_literal_storage import canonical_projected_literal, storage
from .structured_table_bindings import audit_chunk, obj, row_indices, require


class FactSurfaceUnavailable(ValueError):
    """Observed structure cannot supply a verified regular-grid fact window."""


def chunk_key(chunk):
    for key in ('retrievalChunkId', 'chunkId'):
        if chunk.get(key) is not None:
            return str(chunk[key])
    require(type(chunk.get('chunkIndex')) is int, 'fact_surface_chunk_identity_missing')
    return 'index:' + str(chunk['chunkIndex'])


def _identity(document, document_id, revision_id, source_sha256):
    for value in (document_id, revision_id):
        require(isinstance(value, str) and str(uuid.UUID(value)) == value, 'fact_surface_scope_uuid_invalid')
    require(isinstance(source_sha256, str) and re.fullmatch('[a-f0-9]{64}', source_sha256) is not None,
            'fact_surface_source_sha_invalid')
    require(document.get('documentId') == document_id and document.get('revisionId') == revision_id
            and document.get('source', {}).get('sha256') == source_sha256, 'fact_surface_document_scope_mismatch')


def _declared_table(metadata, text=''):
    return metadata.get('chunkComposition') == 'canonical_table_grid_html_v1' or any(
        metadata.get(key) == 'docling_canonical_table_v1' for key in ('chunkType', 'originalChunkType')) \
        or metadata.get('canonicalTableCellMap') is not None \
        or ('<table' in text and 'data-source-table-ordinal=' in text) \
        or '<canonical-table-observations' in text


def _caption_windows(chunk, document, table, html_start, html_end):
    prefix, suffix = chunk['text'][:html_start], chunk['text'][html_end:]
    require(not suffix.strip(), 'fact_surface_unbound_table_suffix')
    if not prefix.strip():
        return []
    metadata = obj(chunk.get('metadata')) or {}
    linked = table.get('captionBlockIds') or []
    context = metadata.get('canonicalContextBlockIds') or chunk.get('canonicalContextBlockIds') or []
    require(isinstance(linked, list) and isinstance(context, list), 'fact_surface_caption_ids_invalid')
    pages = [p for p in document['pages'] if p.get('pageNumber') == chunk['pageStart']]
    require(len(pages) == 1, 'fact_surface_caption_page_ambiguous')
    blocks = sorted([b for b in pages[0].get('blocks', []) if b.get('blockId') in linked],
                    key=lambda b: (b.get('readingOrder', 0), b.get('ordinal', 0)))
    values = []
    for block in blocks:
        identity = block['blockId']
        require(identity in context, 'fact_surface_caption_anchor_missing')
        value = block.get('text', {})
        require(isinstance(value, dict) and isinstance(value.get('retrieval'), str), 'fact_surface_caption_literal_missing')
        text = canonical_projected_literal(value['retrieval'])
        if text.strip():
            values.append((identity, text))
    expected = storage('\n\n'.join(text for _, text in values))
    require(expected.strip() == prefix.strip() and values, 'fact_surface_table_prefix_not_linked_caption')
    return [{'basis': 'explicit_same_page_caption', 'text': text, 'blockIds': [identity]} for identity, text in values]


def validated_chunk_windows(chunk, document, *, document_id, revision_id, source_sha256):
    """Return verified row windows or fail closed for a declared structured grid."""
    _identity(document, document_id, revision_id, source_sha256)
    for key, expected in (('docId', document_id), ('revisionId', revision_id),
                          ('sourceHash', source_sha256), ('sourceSha256', source_sha256)):
        require(key not in chunk or chunk[key] == expected, 'fact_surface_chunk_scope_mismatch')
    metadata = obj(chunk.get('metadata')) or {}
    require(isinstance(metadata, dict) and isinstance(chunk.get('text'), str), 'fact_surface_chunk_invalid')
    metadata = dict(metadata)
    for key in ('chunkType', 'originalChunkType', 'chunkComposition', 'canonicalTableCellMap',
                'canonicalTableCellIds', 'canonicalContextBlockIds'):
        if key in chunk:
            require(key not in metadata or metadata[key] == chunk[key], 'fact_surface_structural_metadata_conflict')
            metadata[key] = chunk[key]
    if 'checksumSha256' in chunk:
        require(hashlib.sha256(chunk['text'].encode('utf-8')).hexdigest() == chunk['checksumSha256'],
                'fact_surface_chunk_checksum_mismatch')
    if not _declared_table(metadata, chunk['text']):
        return [{'basis': 'unchanged_plain_chunk', 'text': chunk['text']}]
    scoped = {**chunk, 'metadata': metadata}
    proof = audit_chunk(scoped, document)
    if proof['state'] in ('EXPLICIT_OVERLAP_LEDGER_NOT_A_REGULAR_GRID', 'LEGACY_MAP_UNAVAILABLE'):
        raise FactSurfaceUnavailable('fact_surface_' + proof['state'].lower())
    require(proof['state'] == 'MAP_HTML_CANONICAL_BINDINGS_CONSISTENT', 'fact_surface_structured_binding_unavailable')
    page, ordinal = proof['physicalPage'], proof['tableOrdinal']
    tables = [t for p in document.get('pages', []) if p.get('pageNumber') == page
              for t in p.get('tables', []) if t.get('ordinal') == ordinal]
    require(len(tables) == 1, 'fact_surface_table_identity_ambiguous')
    table = tables[0]
    start = chunk['text'].index('<table')
    end = chunk['text'].index('</table>', start) + len('</table>')
    root = ET.fromstring(chunk['text'][start:end])
    indices = row_indices(root, table['rowCount'])
    windows = _caption_windows(scoped, document, table, start, end)
    source = {(c['rowIndex'], c['columnIndex']): c for c in table['cells']}
    occupied = {(r, col): c for c in table['cells']
                for r in range(c['rowIndex'], c['rowIndex'] + c['rowSpan'])
                for col in range(c['columnIndex'], c['columnIndex'] + c['columnSpan'])}
    for row, element in zip(indices, root):
        column, values, identities = 0, [], []
        def emit():
            if any(value.strip() for value in values):
                windows.append({'basis': 'verified_source_table_row', 'text': '\t'.join(values),
                                'physicalPage': page, 'tableOrdinal': ordinal, 'rowIndex': row,
                                'cellIds': list(identities)})
            values.clear()
            identities.clear()
        for node in element:
            while column < table['columnCount'] and (row, column) in occupied \
                    and occupied[(row, column)]['rowIndex'] < row:
                emit()
                column += 1
            if node.get('data-canonical-missing') == 'true':
                emit()
                column += 1
                continue
            cell = source[(row, column)]
            values.append(node.text or '')
            identities.append(cell['cellId'])
            column += cell['columnSpan']
        # Do not repeat rowspan values onto a later row, infer header paths,
        # or join neighboring rows. Such relations need explicit annotations.
        emit()
    return windows


def document_source_windows(document, *, document_id, revision_id, source_sha256):
    """Independent canonical page/block and row windows, without cross-row joins."""
    _identity(document, document_id, revision_id, source_sha256)
    result, errors = {}, []
    for page in document.get('pages', []):
        number = page.get('pageNumber')
        require(type(number) is int and number > 0 and number not in result, 'fact_surface_physical_page_invalid')
        # Preserve the existing plain-page text order, separately from tables.
        blocks = []
        for block in page.get('blocks', []):
            value = block.get('text', {})
            text = value.get('retrieval') if isinstance(value, dict) else value
            if isinstance(text, str):
                blocks.append(text)
        result[number] = [{'basis': 'canonical_plain_page_blocks', 'text': '\n'.join(blocks)}]
        ordinals = set()
        for table in page.get('tables', []):
            try:
                ordinal, rows, columns = (table.get(key) for key in ('ordinal', 'rowCount', 'columnCount'))
                require(type(ordinal) is int and ordinal >= 0 and ordinal not in ordinals, 'fact_surface_table_ordinal_invalid')
                ordinals.add(ordinal)
                require(type(rows) is int and 0 < rows <= 10_000 and type(columns) is int and 0 < columns <= 128,
                        'fact_surface_table_dimensions_invalid')
                cells = table.get('cells', [])
                require(isinstance(cells, list) and len(cells) <= 16_384, 'fact_surface_cell_count_bound')
                occupied, ids = {}, set()
                for cell in cells:
                    identity = cell.get('cellId')
                    row, column, rs, cs = (cell.get(key) for key in ('rowIndex', 'columnIndex', 'rowSpan', 'columnSpan'))
                    require(isinstance(identity, str) and identity and identity not in ids, 'fact_surface_cell_id_invalid')
                    require(all(type(value) is int for value in (row, column, rs, cs))
                            and 0 <= row < rows and 0 <= column < columns and 1 <= rs <= rows-row and 1 <= cs <= columns-column,
                            'fact_surface_cell_coordinates_invalid')
                    ids.add(identity)
                    for r in range(row, row+rs):
                        for c in range(column, column+cs):
                            require((r, c) not in occupied, 'fact_surface_source_grid_overlap')
                            occupied[(r, c)] = cell
                for row in range(rows):
                    values, identities, column = [], [], 0
                    def emit():
                        if any(value.strip() for value in values):
                            result[number].append({'basis': 'canonical_source_table_row', 'text': '\t'.join(values),
                                'physicalPage': number, 'tableOrdinal': ordinal, 'rowIndex': row, 'cellIds': list(identities)})
                        values.clear()
                        identities.clear()
                    while column < columns:
                        cell = occupied.get((row, column))
                        if cell is None or cell['rowIndex'] != row:
                            emit()
                            column += 1
                            continue
                        value = cell.get('text', {})
                        require(isinstance(value, dict) and isinstance(value.get('retrieval'), str),
                                'fact_surface_source_literal_unavailable')
                        values.append(canonical_projected_literal(value['retrieval']))
                        identities.append(cell['cellId'])
                        column += cell['columnSpan']
                    emit()
            except (ValueError, KeyError, TypeError) as error:
                # A broken source grid never becomes successful fact evidence.
                errors.append({'physicalPage': number, 'tableOrdinal': table.get('ordinal'),
                               'code': str(error) if isinstance(error, ValueError) else 'fact_surface_source_invalid_shape',
                               'state': 'UNAVAILABLE' if str(error) == 'fact_surface_source_grid_overlap' else 'INVALID'})
    return result, errors


def build_retrieval_windows(document, chunks, *, document_id, revision_id, source_sha256):
    windows, errors = {}, []
    for chunk in chunks:
        key = chunk_key(chunk)
        require(key not in windows, 'fact_surface_duplicate_chunk_identity')
        try:
            windows[key] = validated_chunk_windows(chunk, document, document_id=document_id,
                                                   revision_id=revision_id, source_sha256=source_sha256)
        except (ValueError, KeyError, TypeError, ET.ParseError) as error:
            windows[key] = []
            errors.append({'chunkKey': key, 'code': str(error) if isinstance(error, ValueError) else 'fact_surface_invalid_shape',
                           'state': 'UNAVAILABLE' if isinstance(error, FactSurfaceUnavailable) else 'INVALID'})
    return windows, errors


def has_structured_chunks(chunks):
    for chunk in chunks:
        metadata = obj(chunk.get('metadata')) or {}
        effective = {**chunk, **metadata} if isinstance(metadata, dict) else chunk
        if _declared_table(effective, chunk.get('text', '')):
            return True
    return False


def fact_in_windows(text, windows):
    pattern = re.compile(r'(?<!\w)' + re.escape(exact_text(text)) + r'(?!\w)')
    return any(pattern.search(exact_text(window['text'])) for window in windows)
