using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record LegacyCanonicalProjectionContext(
    Guid DocumentId,
    Guid RevisionId,
    string SourceSha256,
    long SourceSizeBytes,
    string SourceDisplayName,
    string ManifestSha256,
    string StageId,
    string Method,
    string Engine,
    string EngineVersion,
    string? Language = null);

internal static class LegacyPdfCanonicalDocumentAdapter
{
    public static CanonicalDocument Project(
        LegacyCanonicalProjectionContext context,
        PdfExtractionResult extraction,
        IReadOnlyList<ExtractedDocumentSection> sections)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(sections);

        var pages = extraction.Pages
            .OrderBy(static page => page.PageNumber)
            .Select(page => ProjectPage(context, page))
            .ToList();
        var blockIdsByPage = pages.ToDictionary(
            static page => page.PageNumber,
            static page => page.Blocks.Select(block => block.BlockId).ToArray());

        var document = new CanonicalDocument
        {
            DocumentId = context.DocumentId,
            RevisionId = context.RevisionId,
            Source = new()
            {
                Sha256 = context.SourceSha256,
                MediaType = "application/pdf",
                SizeBytes = context.SourceSizeBytes,
                DisplayName = context.SourceDisplayName
            },
            PageCount = pages.Count,
            Pages = pages,
            SectionTree = ProjectSections(context.SourceSha256, sections, blockIdsByPage),
            ManifestSha256 = context.ManifestSha256
        };

        CanonicalContractValidator.ValidateOrThrow(document);
        return document;
    }

    private static CanonicalPage ProjectPage(
        LegacyCanonicalProjectionContext context,
        ExtractedPdfPage page)
    {
        var canonicalText = page.Text ?? "";
        var rawText = page.RawText ?? canonicalText;
        var blockId = CanonicalStableId.Create(
            "block",
            context.SourceSha256,
            page.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToHexString(page.Checksum));
        var qualityFlags = new List<string>
        {
            "legacy_page_text_projection",
            "spatial_blocks_unavailable"
        };
        if (page.RawText is null)
            qualityFlags.Add("legacy_raw_text_unavailable");
        if (page.Quality is not null)
            qualityFlags.AddRange(page.Quality.Signals);

        return new()
        {
            PageNumber = page.PageNumber,
            Geometry = new()
            {
                Width = page.WidthPoints,
                Height = page.HeightPoints,
                Unit = page.WidthPoints.HasValue ? "pt" : null
            },
            Language = context.Language,
            Blocks =
            [
                new()
                {
                    BlockId = blockId,
                    BlockType = "legacy_page_text",
                    Ordinal = 0,
                    ReadingOrder = 0,
                    Text = BuildTextVariants(rawText, canonicalText),
                    QualityFlags = qualityFlags.Distinct(StringComparer.Ordinal).ToList(),
                    Provenance = new()
                    {
                        StageId = context.StageId,
                        Method = context.Method,
                        Engine = context.Engine,
                        EngineVersion = context.EngineVersion,
                        Language = context.Language
                    }
                }
            ],
            QualityFlags = qualityFlags.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static List<CanonicalSectionNode> ProjectSections(
        string sourceSha256,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyDictionary<int, string[]> blockIdsByPage)
    {
        var ordered = sections
            .OrderBy(static section => section.Ordinal)
            .ToArray();
        var sectionIds = ordered.ToDictionary(
            static section => section.Ordinal,
            section => CanonicalStableId.Create(
                "section",
                sourceSha256,
                section.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                section.Title));
        var parentByOrdinal = new Dictionary<int, string?>();
        var ancestors = new Stack<ExtractedDocumentSection>();

        foreach (var section in ordered)
        {
            while (ancestors.Count > 0 && ancestors.Peek().Level >= section.Level)
                ancestors.Pop();

            parentByOrdinal[section.Ordinal] = ancestors.Count == 0
                ? null
                : sectionIds[ancestors.Peek().Ordinal];
            ancestors.Push(section);
        }

        return ordered
            .Select(section => new CanonicalSectionNode
            {
                SectionId = sectionIds[section.Ordinal],
                ParentSectionId = parentByOrdinal[section.Ordinal],
                Ordinal = section.Ordinal,
                Level = section.Level,
                PageStart = section.PageStart,
                PageEnd = section.PageEnd,
                ContentBlockIds = Enumerable
                    .Range(section.PageStart, section.PageEnd - section.PageStart + 1)
                    .Where(blockIdsByPage.ContainsKey)
                    .SelectMany(pageNumber => blockIdsByPage[pageNumber])
                    .ToList()
            })
            .ToList();
    }

    private static CanonicalTextVariants BuildTextVariants(string rawText, string canonicalText)
        => new()
        {
            Raw = rawText,
            Canonical = canonicalText,
            Normalized = Normalize(canonicalText),
            Retrieval = canonicalText,
            Display = canonicalText,
            RawSha256 = Sha256(rawText)
        };

    private static string Normalize(string value)
        => string.Join(
                ' ',
                value
                    .Normalize(NormalizationForm.FormKC)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
