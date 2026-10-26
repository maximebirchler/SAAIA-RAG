using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal static partial class DoclingCanonicalRetrievalProjector
{
    internal const string ContentChunkType = "docling_canonical_content_v1";
    internal const string TableChunkType = "docling_canonical_table_v1";
    internal const string NativeLayoutAlternativeChunkType =
        "docling_canonical_native_layout_alternative_v1";

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
        IReadOnlyList<string> QualitySignals,
        int TableColumnCount = 0,
        int TableRowCount = 0,
        bool TableHasOverlaps = false,
        CanonicalTableRowGroup? TableGroup = null,
        int TableOrdinal = 0,
        IReadOnlyList<SourceAtom>? TableCaptions = null,
        bool IsTableCaption = false);

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
        var atoms = NormalizeHeadingLevelsWithinPages(
            BuildAtoms(document, itemMap, orderByRef)
            .OrderBy(static atom => atom.PageNumber)
            .ThenBy(static atom => atom.GlobalOrder)
            .ThenBy(static atom => atom.SubOrder)
            .ThenBy(static atom => atom.GroupKey, StringComparer.Ordinal)
            .ToArray());
        if (atoms.Length == 0)
            return Array.Empty<ProjectedRetrievalChunk>();

        var projected = new List<ProjectedRetrievalChunk>();
        var activeHeadings = new List<SourceAtom>();
        var pagesContainingHeadings = atoms
            .Where(static atom => atom.IsHeading)
            .Select(static atom => atom.PageNumber)
            .ToHashSet();
        var sectionPageEndByOrdinal = document.SectionTree.ToDictionary(
            static section => section.Ordinal,
            static section => section.PageEnd);
        var pagesWithUncertainLayoutAssociations = document.Pages
            .Where(page => page.QualityFlags.Contains(CanonicalSourceAssociationPolicy.LayoutAssociationUncertainFlag,
                StringComparer.Ordinal))
            .Select(page => page.PageNumber).ToHashSet();
        int? currentPageNumber = null;
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
            if (!string.Equals(
                    chunkType,
                    NativeLayoutAlternativeChunkType,
                    StringComparison.Ordinal)
                && !string.Equals(chunkType, RetrievalChunkProjector.PhysicalPageContextChunkType, StringComparison.Ordinal))
            {
                foreach (var heading in SelectContextHeadings(
                             activeHeadings,
                             content.IsNavigation,
                             maxWords,
                             CountTokens(content.Text)))
                {
                    next.Add(heading, isContextHeading: true);
                }
            }

            return next;
        }

        for (var index = 0; index < atoms.Length;)
        {
            var atom = atoms[index];
            if (currentPageNumber != atom.PageNumber)
            {
                Flush();
                currentPageNumber = atom.PageNumber;
                // A page with its own headings starts a new local reading
                // context. A headingless page may inherit only a section that
                // the canonical tree proves continues onto that page.
                if (pagesContainingHeadings.Contains(atom.PageNumber)
                    || pagesWithUncertainLayoutAssociations.Contains(atom.PageNumber))
                {
                    activeHeadings.Clear();
                }
                else
                {
                    activeHeadings.RemoveAll(heading =>
                        !heading.SectionOrdinal.HasValue
                        || !sectionPageEndByOrdinal.TryGetValue(
                            heading.SectionOrdinal.Value,
                            out var pageEnd)
                        || pageEnd < atom.PageNumber);
                }
            }

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
        RetainUncoveredObservedHeadings(atoms, projected, maxWords);
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
            var observations = CanonicalObservationSelector.SelectPage(page);
            foreach (var block in observations.Blocks)
            {
                if (IsProvenPageCounter(block, page.PageNumber)
                    || string.IsNullOrWhiteSpace(block.Text.Retrieval))
                    continue;
                if (IsNativeSupplementalBlock(block))
                {
                    atoms.Add(new(
                        observations.ResolveOrder(block.BlockId, int.MaxValue / 2 + block.ReadingOrder),
                        block.Ordinal,
                        page.PageNumber,
                        NormalizeText(block.Text.Retrieval),
                        string.Equals(block.BlockType, CanonicalNativeTextCoverageReconciler.RecoveryBlockType, StringComparison.Ordinal)
                            ? "unassigned_physical_context" : block.BlockType,
                        block.BlockId,
                        false,
                        false,
                        1,
                        null,
                        false,
                        [block.BlockId],
                        block.Spans
                            .Select(static span => span.SpanId)
                            .ToArray(),
                        [],
                        CanonicalSourceQuality.Combine(page, block.QualityFlags)));
                    continue;
                }

                var sourceRef = RequireSourceRef(block.Provenance);
                var provenanceIndex = ResolveProvenanceIndex(block.Provenance);
                var item = RequireItem(itemMap, sourceRef);
                var unassigned = CanonicalSourceAssociationPolicy.IsUnassignedPhysicalContext(block);
                var navigation = !unassigned && IsNavigationItem(item, itemMap);
                var heading = !unassigned && IsHeadingBlock(block.BlockType);
                atoms.Add(new(
                    observations.ResolveOrder(block.BlockId, DoclingDocumentTraversal.ResolveOrder(
                        orderByRef,
                        sourceRef,
                        provenanceIndex)),
                    block.ReadingOrder,
                    page.PageNumber,
                    NormalizeText(block.Text.Retrieval),
                    unassigned ? "unassigned_physical_context" : heading ? "heading" : "block",
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
                    CanonicalSourceQuality.Combine(page, block.QualityFlags)));
            }

            foreach (var table in observations.Tables)
            {
                var sourceRef = RequireSourceRef(table.Provenance);
                var provenanceIndex = ResolveProvenanceIndex(table.Provenance);
                var item = RequireItem(itemMap, sourceRef);
                var navigation = IsNavigationItem(item, itemMap);
                atoms.AddRange(BuildTableAtoms(table, page, navigation,
                    observations.ResolveOrder(table.TableId,
                        DoclingDocumentTraversal.ResolveOrder(orderByRef, sourceRef, provenanceIndex))));
            }
        }

        return atoms;
    }

    private static SourceAtom[] NormalizeHeadingLevelsWithinPages(
        SourceAtom[] atoms)
    {
        // Docling heading levels can be visual rather than semantic. In
        // particular, a late instruction can be labelled level 1 while the
        // real page title is level 6. Rebase levels within each page so
        // consecutive headings can form a local title/subtitle hierarchy,
        // while peer headings separated by content remain peers.
        int? currentPageNumber = null;
        var firstHeadingLevel = 1;
        var hasPageHeading = false;
        var hasPageSubheading = false;
        var hasContentAfterPageHeading = false;
        for (var index = 0; index < atoms.Length; index++)
        {
            var atom = atoms[index];
            if (currentPageNumber != atom.PageNumber)
            {
                currentPageNumber = atom.PageNumber;
                firstHeadingLevel = 1;
                hasPageHeading = false;
                hasPageSubheading = false;
                hasContentAfterPageHeading = false;
            }

            if (!atom.IsHeading)
            {
                if (hasPageHeading)
                    hasContentAfterPageHeading = true;
                continue;
            }

            if (!hasPageHeading)
            {
                firstHeadingLevel = atom.HeadingLevel;
                hasPageHeading = true;
                atoms[index] = atom with { HeadingLevel = 1 };
                continue;
            }

            var relativeLevel = 1;
            if (atom.HeadingLevel > firstHeadingLevel)
            {
                relativeLevel = 1 + (atom.HeadingLevel - firstHeadingLevel);
                hasPageSubheading = true;
            }
            else if (!hasContentAfterPageHeading || hasPageSubheading)
            {
                relativeLevel = 2;
                hasPageSubheading = true;
            }
            else
            {
                firstHeadingLevel = atom.HeadingLevel;
                hasContentAfterPageHeading = false;
                hasPageSubheading = false;
            }

            atoms[index] = atom with
            {
                HeadingLevel = Math.Clamp(relativeLevel, 1, 16)
            };
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

    private static bool IsProvenPageCounter(CanonicalBlock block, int physicalPage)
        => (block.IsRepeatedFurniture
                || string.Equals(block.BlockType, "page_header", StringComparison.OrdinalIgnoreCase)
                || string.Equals(block.BlockType, "page_footer", StringComparison.OrdinalIgnoreCase))
           && string.Equals(block.Text.Retrieval.Trim(), physicalPage.ToString(
               System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

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
        var tableProjection = string.Equals(buffer.ChunkType, TableChunkType, StringComparison.Ordinal)
            ? CreateTableProjection(atoms) : null;
        text = tableProjection is not null
            ? RenderTableChunkText(tableProjection)
            : NormalizeText(text);
        var evidenceAtoms = atoms
            .Where(static atom => !atom.IsHeading && !atom.IsTableCaption)
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

        var quality = CanonicalSourceQuality.ForRetrievalText(text, CountTokens(text),
            new[] { "document_intelligence_docling", "canonical_source_links" }
                .Concat(atoms.SelectMany(static atom => atom.QualitySignals))
                .Concat(atoms.SelectMany(static atom => atom.TableCaptions ?? [])
                    .SelectMany(static caption => caption.QualitySignals)));

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
            ExtractionTextStatus: quality.TextStatus,
            ExtractionTextSparse: quality.TextSparse,
            ExtractionOcrCandidate: quality.OcrCandidate,
            ExtractionQualitySignals: quality.Signals,
            SourceUnitOrdinals: [],
            SourceUnitCount: 0,
            ChunkComposition: string.Equals(buffer.ChunkType, RetrievalChunkProjector.PhysicalPageContextChunkType, StringComparison.Ordinal)
                ? "canonical_physical_context_not_asserted"
                : string.Equals(
                buffer.ChunkType,
                TableChunkType,
                StringComparison.Ordinal)
                ? tableProjection?.HasOverlappingCells == true
                    ? "canonical_table_overlap_observations_v1"
                    : "canonical_table_grid_html_v1"
                : string.Equals(
                    buffer.ChunkType,
                    NativeLayoutAlternativeChunkType,
                    StringComparison.Ordinal)
                    ? "canonical_native_layout_alternative"
                    : "canonical_blocks",
            CanonicalBlockIds: Distinct(
                sourceAtoms.SelectMany(static atom => atom.BlockIds)),
            CanonicalSpanIds: Distinct(
                sourceAtoms.SelectMany(static atom => atom.SpanIds)),
            CanonicalTableCellIds: Distinct(
                sourceAtoms.SelectMany(static atom => atom.TableCellIds)),
            CanonicalContextBlockIds: Distinct(
                atoms
                    .Where(static atom => atom.IsHeading || atom.IsTableCaption)
                    .SelectMany(static atom => atom.BlockIds)),
            SectionTitle: sectionHeading?.Text,
            HeadingPath: string.IsNullOrWhiteSpace(headingPath)
                ? null
                : headingPath,
            HeadingLevel: sectionHeading?.HeadingLevel,
            StructuredTableProjection: tableProjection);
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
            if (IsStructuredChunk(current) || IsStructuredChunk(previous)
                || current.TokenCount >= minWords
                || current.ContentRole != previous.ContentRole
                || current.ChunkType != previous.ChunkType
                || current.SectionOrdinal != previous.SectionOrdinal
                || !string.Equals(
                    current.HeadingPath,
                    previous.HeadingPath,
                    StringComparison.Ordinal)
                || current.PageStart != previous.PageEnd
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
            if (IsStructuredChunk(current) || IsStructuredChunk(next)
                || current.TokenCount >= minWords
                || current.ContentRole != next.ContentRole
                || current.ChunkType != next.ChunkType
                || current.SectionOrdinal != next.SectionOrdinal
                || !string.Equals(
                    current.HeadingPath,
                    next.HeadingPath,
                    StringComparison.Ordinal)
                || next.PageStart != current.PageEnd
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
    {
        if (string.Equals(
                atom.Kind,
                "table_row",
                StringComparison.Ordinal))
        {
            return TableChunkType;
        }

        if (string.Equals(atom.Kind, "unassigned_physical_context", StringComparison.Ordinal))
            return RetrievalChunkProjector.PhysicalPageContextChunkType;

        return string.Equals(
            atom.Kind,
            CanonicalNativeLayoutReconciler.RecoveryBlockType,
            StringComparison.Ordinal)
            ? NativeLayoutAlternativeChunkType
            : ContentChunkType;
    }

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
            if (IsNavigationLabel(current.Label)
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
    {
        var normalized = string.Join(
            Environment.NewLine,
            (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => string.Join("\t", line.Split('\t').Select(static cell => string.Join(
                " ", cell.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))))
            .Where(static line => !string.IsNullOrWhiteSpace(line)))
            .Trim();
        return RetrievalTextHygiene.RepairHyphenatedCompoundSpacing(normalized);
    }

    private static int CountTokens(string? value)
        => (value ?? "").Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).Length;
}
