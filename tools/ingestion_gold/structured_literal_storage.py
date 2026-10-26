"""Mechanical mirror of the declared table-projection/storage transformations.

No case folding, NFC, fuzzy match, lexical correction or semantic equivalence.
The raw canonical value and the actual SQL HTML remain independent inputs.
"""
import re
import unicodedata


def storage(text):
    value = text.replace('\0', ' ').replace('\r\n', '\n').replace('\r', '\n')
    value = ''.join(' ' if unicodedata.category(c) == 'Zs' or c == '\t' else c for c in value)
    value = re.sub(' +', ' ', value).replace('\u00ad', '')
    value = re.sub('[\x01-\x08\x0b\x0c\x0e-\x1f]+', ' ', value)
    value = re.sub(' *\n *', '\n', value)
    return re.sub('\n{4,}', '\n\n\n', value).replace('\0', '')


def repair_declared_hyphen_spacing(text):
    # Exact category rule of RetrievalTextHygiene: L[L,M]* [-,2010,2011]
    # whitespace Ll[L,M]*. Only whitespace after that observed hyphen moves.
    remove = set()
    for i, character in enumerate(text):
        if character not in '-\u2010\u2011' or i == 0:
            continue
        left = i - 1
        while left >= 0 and unicodedata.category(text[left])[0] in 'LM':
            left -= 1
        if left == i - 1 or not any(unicodedata.category(c)[0] == 'L' for c in text[left + 1:i]):
            continue
        right = i + 1
        while right < len(text) and text[right].isspace():
            right += 1
        if right > i + 1 and right < len(text) and unicodedata.category(text[right]) == 'Ll':
            remove.update(range(i + 1, right))
    return ''.join(c for i, c in enumerate(text) if i not in remove)


def canonical_projected_literal(text):
    # Tables.cs sanitizes a retrieval cell before NormalizeText, then the
    # repository sanitizes the final projected literal again before hashing.
    value = storage(text)
    lines = [' '.join(line.split()) for line in value.split('\n')]
    value = '\n'.join(line for line in lines if line.strip()).strip()
    return storage(repair_declared_hyphen_spacing(value))
