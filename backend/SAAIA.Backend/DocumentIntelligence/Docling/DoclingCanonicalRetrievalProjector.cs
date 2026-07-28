using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal static class DoclingCanonicalRetrievalProjector
{
    internal const string ContentChunkType = "docling_canonical_content_v1";
    internal const string TableChunkType = "docling_canonical_table_v1";

    private sealed record SourceAtom(
        int GlobalOrder,
        int SubOrder,
        int PageNumber,
        string Text,
        string Kind,
        string GroupKey,
        bool IsNavigation,
        bool IsHeading,
        int HeadingLevel,
        int? SectionOrdinal,
        bool IsTableHeader,
        IReadOnlyList<string> BlockIds,
        IReadOnlyList<string> SpanIds,
        IReadOnlyList<string> TableCellIds,
        IReadOnlyList<string> QualitySignals);

    private sealed class ChunkBuffer(
        bool isNavigation,
        string chunkType)
    {
        public bool IsNavigation { get; } = isNavigation;
        public string ChunkType { get; } = chunkType;
        public List<SourceAtom> Atoms { get; } = [];
        public int TokenCount { get; private set; }
        public bool HasContent { get; private set; }

        public bool CanAdd(SourceAtom atom, int maxWords)
            => !HasContent || TokenCount + CountTokens(atom.Text) <= maxWords;

        public void Add(SourceAtom atom, bool isContextHeading = false)
        {
            Atoms.Add(atom);
            TokenCount += CountTokens(atom.Text);
            HasContent |= !isContextHeading;
        }
    }

    public static IReadOnlyList<ProjectedRetrievalChunk> Project(
        CanonicalDocument document,
        DoclingDocument source,
        int maxWords,
        int minWords)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        CanonicalContractValidator.ValidateOrThrow(document);
        maxWords = Math.Max(1, maxWords);
        minWords = Math.Clamp(minWords, 1, maxWords);

        var itemMap = DoclingDocumentTraversal.BuildItemMap(source);
        var orderByRef = DoclingDocumentTraversal.BuildReadingOrder(
            source,
            itemMap);
        var atoms = BuildAtoms(document, itemMap, orderByRef)
            .OrderBy(static atom => atom.PageNumber)
            .ThenBy(static atom => atom.GlobalOrder)
            .ThenBy(static atom => atom.SubOrder)
            .ThenBy(static atom => atom.GroupKey, StringComparer.Ordinal)
            .ToArray();
        if (atoms.Length == 0)
            return Array.Empty<ProjectedRetrievalChunk>();

        var projected = new List<ProjectedRetrievalChunk>();
        var activeHeadings = new List<SourceAtom>();
        ChunkBuffer? buffer = null;

        void Flush()
        {
            if (buffer is null || !buffer.HasContent)
            {
                buffer = null;
                return;
            }

            projected.Add(CreateChunk(projected.Count, buffer));
            buffer = null;
        }

        ChunkBuffer StartBuffer(
            SourceAtom content,
            string chunkType)
        {
            var next = new ChunkBuffer(content.IsNavigation, chunkType);
            foreach (var heading in SelectContextHeadings(
                         activeHeadings,
                         content.IsNavigation,
                         maxWords,
                         CountTokens(content.Text)))
            {
                next.Add(heading, isContextHeading: true);
            }

            return next;
        }

        for (var index = 0; index < atoms.Length;)
        {
            var atom = atoms[index];
            if (atom.IsHeading)
            {
                Flush();
                while (activeHeadings.Count > 0
                       && activeHeadings[^1].HeadingLevel >= atom.HeadingLevel)
                {
                    activeHeadings.RemoveAt(activeHeadings.Count - 1);
                }

                activeHeadings.Add(atom);
                index++;
                continue;
            }

            if (string.Equals(atom.Kind, "table_row", StringComparison.Ordinal))
            {
                Flush();
                var tableRows = new List<SourceAtom>();
                var tableKey = atom.GroupKey;
                while (index < atoms.Length
                       && string.Equals(
                           atoms[index].Kind,
                           "table_row",
                           StringComparison.Ordinal)
                       && string.Equals(
                           atoms[index].GroupKey,
                           tableKey,
                           StringComparison.Ordinal))
                {
                    tableRows.Add(atoms[index]);
                    index++;
                }

                EmitTableChunks(
                    tableRows,
                    activeHeadings,
                    maxWords,
                    projected);
                continue;
            }

            foreach (var part in SplitOversizedAtom(atom, maxWords))
            {
                if (buffer is null
                    || buffer.IsNavigation != part.IsNavigation
                    || !string.Equals(
                        buffer.ChunkType,
                        ResolveChunkType(part),
                        StringComparison.Ordinal))
                {
                    Flush();
                    buffer = StartBuffer(part, ResolveChunkType(part));
                }

                if (!buffer.CanAdd(part, maxWords))
                {
                    Flush();
                    buffer = StartBuffer(part, ResolveChunkType(part));
                }

                buffer.Add(part);
            }

            index++;
        }

        Flush();
        MergeSmallAdjacentChunks(projected, maxWords, minWords);
        return projected
            .Select((chunk, index) => chunk with
            {
                ChunkIndex = index,
                Checksum = SHA256.HashData(
                    Encoding.UTF8.GetBytes(chunk.Text))
            })
            .ToArray();
    }

    private static IReadOnlyList<SourceAtom> BuildAtoms(
        CanonicalDocument document,
        IReadOnlyDictionary<string, DoclingContentItem> itemMap,
        IReadOnlyDictionary<string, int> orderByRef)
    {
        var atoms = new List<SourceAtom>();
        var sectionOrdinalByTitleBlockId = document.SectionTree
            .SelectMany(section => section.TitleBlockIds.Select(
                blockId => new
                {
                    BlockId = blockId,
                    section.Ordinal
                }))
            .ToDictionary(
                static item => item.BlockId,
                static item => item.Ordinal,
                StringComparer.Ordinal);
        foreach (var page in document.Pages)
        {
            foreach (var block in page.Blocks)
            {
                if (block.IsRepeatedFurniture
                    || string.IsNullOrWhiteSpace(block.Text.Retrieval))
                    continue;
                if (IsNativeSupplementalBlock(block))
                {
                    atoms.Add(new(
                        int.MaxValue / 2 + block.ReadingOrder,
                        block.Ordinal,
                        page.PageNumber,
                        NormalizeText(block.Text.Retrieval),
                        block.BlockType,
                        block.BlockId,
                        IsNativeSupplementalNavigation(block),
                        false,
                        1,
                        null,
                        false,
                        [block.BlockId],
                        block.Spans
                            .Select(static span => span.SpanId)
                            .ToArray(),
                        [],
                        block.QualityFlags.ToArray()));
                    continue;
                }

                var sourceRef = RequireSourceRef(block.Provenance);
                var provenanceIndex = ResolveProvenanceIndex(block.Provenance);
                var item = RequireItem(itemMap, sourceRef);
                var navigation = block.IsRepeatedFurniture
                                 || IsNavigationItem(item, itemMap);
                var heading = IsHeadingBlock(block.BlockType);
                atoms.Add(new(
                    DoclingDocumentTraversal.ResolveOrder(
                        orderByRef,
                        sourceRef,
                        provenanceIndex),
                    block.ReadingOrder,
                    page.PageNumber,
                    NormalizeText(block.Text.Retrieval),
                    heading ? "heading" : "block",
                    block.BlockId,
                    navigation,
                    heading,
                    ResolveHeadingLevel(item),
                    heading
                    && sectionOrdinalByTitleBlockId.TryGetValue(
                        block.BlockId,
                        out var sectionOrdinal)
                        ? sectionOrdinal
                        : null,
                    false,
                    [block.BlockId],
                    block.Spans.Select(static span => span.SpanId).ToArray(),
                    [],
                    []));
            }

            foreach (var table in page.Tables)
            {
                var sourceRef = RequireSourceRef(table.Provenance);
                var provenanceIndex = ResolveProvenanceIndex(table.Provenance);
                var item = RequireItem(itemMap, sourceRef);
                var navigation = IsNavigationItem(item, itemMap);
                for (var rowIndex = 0; rowIndex < table.RowCount; rowIndex++)
                {
                    var cells = table.Cells
                        .Where(cell => cell.RowIndex == rowIndex)
                        .OrderBy(static cell => cell.ColumnIndex)
                        .ThenBy(static cell => cell.CellId, StringComparer.Ordinal)
                        .ToArray();
                    var text = string.Join(
                        " | ",
                        cells.Select(static cell => cell.Text.Retrieval)
                            .Where(static value =>
                                !string.IsNullOrWhiteSpace(value)));
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    atoms.Add(new(
                        DoclingDocumentTraversal.ResolveOrder(
                            orderByRef,
                            sourceRef,
                            provenanceIndex),
                        rowIndex,
                        page.PageNumber,
                        NormalizeText(text),
                        "table_row",
                        table.TableId,
                        navigation,
                        false,
                        1,
                        null,
                        cells.Any(static cell =>
                            string.Equals(
                                cell.CellRole,
                                "column_header",
                                StringComparison.Ordinal)
                            || string.Equals(
                                cell.CellRole,
                                "row_and_column_header",
                                StringComparison.Ordinal)),
                        [],
                        [],
                        cells.Select(static cell => cell.CellId).ToArray(),
                        page.QualityFlags
                            .Where(static flag => flag.StartsWith(
                                "canonical_table_",
                                StringComparison.Ordinal))
                            .ToArray()));
                }
            }
        }

        return atoms;
    }

    private static bool IsNativeSupplementalBlock(
        CanonicalBlock block)
        => (string.Equals(
                block.BlockType,
                CanonicalNativeTextCoverageReconciler.RecoveryBlockType,
                StringComparison.Ordinal)
            && string.Equals(
                block.Provenance.Method,
                "native_text_coverage_reconciliation",
                StringComparison.Ordinal))
           || (string.Equals(
                   block.BlockType,
                   CanonicalNativeLayoutReconciler.RecoveryBlockType,
                   StringComparison.Ordinal)
               && string.Equals(
                   block.Provenance.Method,
                   "native_layout_reading_order_reconciliation",
                   StringComparison.Ordinal));

    private static bool IsNativeSupplementalNavigation(
        CanonicalBlock block)
        => block.Provenance.Attributes.TryGetValue(
               "contentRoleHint",
               out var value)
           && string.Equals(
               value,
               RetrievalContentClassifier.NavigationRole,
               StringComparison.OrdinalIgnoreCase);

    private static void EmitTableChunks(
        IReadOnlyList<SourceAtom> rows,
        IReadOnlyList<SourceAtom> activeHeadings,
        int maxWords,
        ICollection<ProjectedRetrievalChunk> projected)
    {
        if (rows.Count == 0)
            return;
        var headerRows = rows
            .Where(static row => row.IsTableHeader)
            .ToArray();
        var dataRows = rows
            .Where(static row => !row.IsTableHeader)
            .ToArray();
        if (dataRows.Length == 0)
            dataRows = rows.ToArray();

        ChunkBuffer Start(SourceAtom row)
        {
            var next = new ChunkBuffer(row.IsNavigation, ResolveChunkType(row));
            foreach (var heading in SelectContextHeadings(
                         activeHeadings,
                         row.IsNavigation,
                         maxWords,
                         CountTokens(row.Text)))
            {
                next.Add(heading, isContextHeading: true);
            }

            if (!headerRows.Contains(row))
            {
                foreach (var header in headerRows)
                {
                    if (next.TokenCount + CountTokens(header.Text) <= maxWords)
                        next.Add(header, isContextHeading: true);
                }
            }

            return next;
        }

        ChunkBuffer? buffer = null;
        foreach (var row in dataRows.SelectMany(row =>
                     SplitOversizedAtom(row, maxWords)))
        {
            buffer ??= Start(row);
            if (!buffer.CanAdd(row, maxWords))
            {
                if (buffer.HasContent)
                    projected.Add(CreateChunk(projected.Count, buffer));
                buffer = Start(row);
            }
            buffer.Add(row);
        }

        if (buffer is { HasContent: true })
            projected.Add(CreateChunk(projected.Count, buffer));
    }

    private static IEnumerable<SourceAtom> SplitOversizedAtom(
        SourceAtom atom,
        int maxWords)
    {
        var tokens = atom.Text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= maxWords)
        {
            yield return atom;
            yield break;
        }

        for (var offset = 0; offset < tokens.Length; offset += maxWords)
        {
            yield return atom with
            {
                SubOrder = atom.SubOrder + (offset / maxWords),
                Text = string.Join(
                    " ",
                    tokens.Skip(offset).Take(maxWords))
            };
        }
    }

    private static IReadOnlyList<SourceAtom> SelectContextHeadings(
        IReadOnlyList<SourceAtom> headings,
        bool isNavigation,
        int maxWords,
        int contentWords)
    {
        var selected = new List<SourceAtom>();
        var available = Math.Max(0, maxWords - contentWords);
        for (var index = headings.Count - 1; index >= 0; index--)
        {
            var heading = headings[index];
            if (heading.IsNavigation != isNavigation)
                continue;
            var words = CountTokens(heading.Text);
            if (words > available)
                continue;
            selected.Add(heading);
            available -= words;
        }

        selected.Reverse();
        return selected;
    }

    private static ProjectedRetrievalChunk CreateChunk(
        int chunkIndex,
        ChunkBuffer buffer)
    {
        var atoms = buffer.Atoms;
        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            atoms.Select(static atom => atom.Text)
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        text = NormalizeText(text);
        var evidenceAtoms = atoms
            .Where(static atom => !atom.IsHeading)
            .ToArray();
        IReadOnlyList<SourceAtom> sourceAtoms = evidenceAtoms.Length == 0
            ? atoms
            : evidenceAtoms;
        var pages = sourceAtoms
            .Select(static atom => atom.PageNumber)
            .Distinct()
            .Order()
            .ToArray();
        var chunkType = buffer.IsNavigation
            ? RetrievalContentClassifier.NavigationChunkType
            : buffer.ChunkType;
        var contentRole = buffer.IsNavigation
            ? RetrievalContentClassifier.NavigationRole
            : RetrievalContentClassifier.ContentRole;
        var contextHeadings = atoms
            .Where(static atom => atom.IsHeading)
            .ToArray();
        var sectionHeading = contextHeadings
            .LastOrDefault(static atom =>
                atom.SectionOrdinal.HasValue);
        var headingPath = string.Join(
            " > ",
            contextHeadings
                .Select(static atom => atom.Text)
                .Where(static value =>
                    !string.IsNullOrWhiteSpace(value)));

        return new(
            ChunkIndex: chunkIndex,
            SectionOrdinal: sectionHeading?.SectionOrdinal,
            UnitOrdinal: null,
            PageStart: pages[0],
            PageEnd: pages[^1],
            Text: text,
            TokenCount: CountTokens(text),
            Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(text)),
            ChunkType: chunkType,
            ContentRole: contentRole,
            NavigationReason: buffer.IsNavigation
                ? "docling_structural_navigation"
                : null,
            OriginalChunkType: buffer.IsNavigation
                ? buffer.ChunkType
                : null,
            NavigationScore: buffer.IsNavigation ? 1.0 : 0.0,
            ContentDensityScore: buffer.IsNavigation ? 0.0 : 1.0,
            ExtractionTextStatus: "ok",
            ExtractionTextSparse: false,
            ExtractionOcrCandidate: false,
            ExtractionQualitySignals: Distinct(
                new[]
                {
                    "document_intelligence_docling",
                    "canonical_source_links"
                }.Concat(sourceAtoms.SelectMany(
                    static atom => atom.QualitySignals))),
            SourceUnitOrdinals: [],
            SourceUnitCount: 0,
            ChunkComposition: string.Equals(
                buffer.ChunkType,
                TableChunkType,
                StringComparison.Ordinal)
                ? "canonical_table_rows"
                : "canonical_blocks",
            CanonicalBlockIds: Distinct(
                sourceAtoms.SelectMany(static atom => atom.BlockIds)),
            CanonicalSpanIds: Distinct(
                sourceAtoms.SelectMany(static atom => atom.SpanIds)),
            CanonicalTableCellIds: Distinct(
                sourceAtoms.SelectMany(static atom => atom.TableCellIds)),
            CanonicalContextBlockIds: Distinct(
                atoms
                    .Where(static atom => atom.IsHeading)
                    .SelectMany(static atom => atom.BlockIds)),
            SectionTitle: sectionHeading?.Text,
            HeadingPath: string.IsNullOrWhiteSpace(headingPath)
                ? null
                : headingPath);
    }

    private static void MergeSmallAdjacentChunks(
        List<ProjectedRetrievalChunk> chunks,
        int maxWords,
        int minWords)
    {
        for (var index = chunks.Count - 1; index > 0; index--)
        {
            var current = chunks[index];
            var previous = chunks[index - 1];
            if (current.TokenCount >= minWords
                || current.ContentRole != previous.ContentRole
                || current.ChunkType != previous.ChunkType
                || current.SectionOrdinal != previous.SectionOrdinal
                || !string.Equals(
                    current.HeadingPath,
                    previous.HeadingPath,
                    StringComparison.Ordinal)
                || current.PageStart > previous.PageEnd + 1
                || current.TokenCount + previous.TokenCount > maxWords)
            {
                continue;
            }

            var mergedText = NormalizeText(
                previous.Text
                + Environment.NewLine
                + Environment.NewLine
                + current.Text);
            chunks[index - 1] = previous with
            {
                PageEnd = Math.Max(previous.PageEnd, current.PageEnd),
                Text = mergedText,
                TokenCount = CountTokens(mergedText),
                Checksum = SHA256.HashData(
                    Encoding.UTF8.GetBytes(mergedText)),
                CanonicalBlockIds = Distinct(
                    (previous.CanonicalBlockIds ?? [])
                    .Concat(current.CanonicalBlockIds ?? [])),
                CanonicalSpanIds = Distinct(
                    (previous.CanonicalSpanIds ?? [])
                    .Concat(current.CanonicalSpanIds ?? [])),
                CanonicalTableCellIds = Distinct(
                    (previous.CanonicalTableCellIds ?? [])
                    .Concat(current.CanonicalTableCellIds ?? [])),
                CanonicalContextBlockIds = Distinct(
                    (previous.CanonicalContextBlockIds ?? [])
                    .Concat(current.CanonicalContextBlockIds ?? []))
            };
            chunks.RemoveAt(index);
        }

        for (var index = 0; index < chunks.Count - 1;)
        {
            var current = chunks[index];
            var next = chunks[index + 1];
            if (current.TokenCount >= minWords
                || current.ContentRole != next.ContentRole
                || current.ChunkType != next.ChunkType
                || current.SectionOrdinal != next.SectionOrdinal
                || !string.Equals(
                    current.HeadingPath,
                    next.HeadingPath,
                    StringComparison.Ordinal)
                || next.PageStart > current.PageEnd + 1
                || current.TokenCount + next.TokenCount > maxWords)
            {
                index++;
                continue;
            }

            var mergedText = NormalizeText(
                current.Text
                + Environment.NewLine
                + Environment.NewLine
                + next.Text);
            chunks[index] = current with
            {
                PageEnd = Math.Max(current.PageEnd, next.PageEnd),
                Text = mergedText,
                TokenCount = CountTokens(mergedText),
                Checksum = SHA256.HashData(
                    Encoding.UTF8.GetBytes(mergedText)),
                CanonicalBlockIds = Distinct(
                    (current.CanonicalBlockIds ?? [])
                    .Concat(next.CanonicalBlockIds ?? [])),
                CanonicalSpanIds = Distinct(
                    (current.CanonicalSpanIds ?? [])
                    .Concat(next.CanonicalSpanIds ?? [])),
                CanonicalTableCellIds = Distinct(
                    (current.CanonicalTableCellIds ?? [])
                    .Concat(next.CanonicalTableCellIds ?? [])),
                CanonicalContextBlockIds = Distinct(
                    (current.CanonicalContextBlockIds ?? [])
                    .Concat(next.CanonicalContextBlockIds ?? []))
            };
            chunks.RemoveAt(index + 1);
        }
    }

    private static string ResolveChunkType(SourceAtom atom)
        => string.Equals(atom.Kind, "table_row", StringComparison.Ordinal)
            ? TableChunkType
            : ContentChunkType;

    private static bool IsHeadingBlock(string? blockType)
        => string.Equals(
               blockType,
               "section_header",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               blockType,
               "title",
               StringComparison.OrdinalIgnoreCase);

    private static int ResolveHeadingLevel(DoclingContentItem item)
        => item is DoclingTextItem text
            ? Math.Clamp(text.Level ?? 1, 1, 16)
            : 1;

    private static bool IsNavigationItem(
        DoclingContentItem item,
        IReadOnlyDictionary<string, DoclingContentItem> itemMap)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var current = item; current is not null;)
        {
            if (!visited.Add(current.SelfRef))
                throw new InvalidDataException(
                    $"Docling parent tree contains a cycle at '{current.SelfRef}'.");
            if (string.Equals(
                    current.ContentLayer,
                    "furniture",
                    StringComparison.OrdinalIgnoreCase)
                || IsNavigationLabel(current.Label)
                || current is DoclingGroup group
                && (IsNavigationLabel(group.Name)
                    || IsNavigationLabel(group.Label)))
            {
                return true;
            }

            if (current.Parent is null)
                break;
            var parentRef = current.Parent.Ref;
            if (string.Equals(
                    parentRef,
                    "#/furniture",
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (string.Equals(
                    parentRef,
                    "#/body",
                    StringComparison.Ordinal))
            {
                break;
            }
            if (!itemMap.TryGetValue(parentRef, out current))
                throw new InvalidDataException(
                    $"Docling parent reference '{parentRef}' cannot be resolved.");
        }

        return false;
    }

    private static bool IsNavigationLabel(string? value)
        => string.Equals(
               value,
               "document_index",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               value,
               "page_header",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               value,
               "page_footer",
               StringComparison.OrdinalIgnoreCase);

    private static string RequireSourceRef(
        CanonicalExtractionProvenance provenance)
        => provenance.Attributes.TryGetValue(
               "doclingSelfRef",
               out var value)
           && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException(
                "Canonical Docling provenance is missing doclingSelfRef.");

    private static int ResolveProvenanceIndex(
        CanonicalExtractionProvenance provenance)
        => provenance.Attributes.TryGetValue(
               "doclingProvenanceIndex",
               out var value)
           && int.TryParse(
               value,
               System.Globalization.NumberStyles.Integer,
               System.Globalization.CultureInfo.InvariantCulture,
               out var index)
            ? Math.Max(0, index)
            : 0;

    private static DoclingContentItem RequireItem(
        IReadOnlyDictionary<string, DoclingContentItem> itemMap,
        string sourceRef)
        => itemMap.TryGetValue(sourceRef, out var item)
            ? item
            : throw new InvalidDataException(
                $"Canonical provenance reference '{sourceRef}' cannot be resolved.");

    private static string[] Distinct(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeText(string? value)
        => string.Join(
            Environment.NewLine,
            (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => string.Join(
                " ",
                line.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)))
            .Where(static line => !string.IsNullOrWhiteSpace(line)))
            .Trim();

    private static int CountTokens(string? value)
        => (value ?? "").Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).Length;
}
