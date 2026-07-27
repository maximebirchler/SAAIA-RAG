using SAAIA.Contracts.DocumentIntelligence;

internal static class CanonicalDocumentSectionProjector
{
    public static IReadOnlyList<ExtractedDocumentSection> Project(
        CanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        CanonicalContractValidator.ValidateOrThrow(document);
        if (document.SectionTree.Count == 0)
            return Array.Empty<ExtractedDocumentSection>();

        var titleByBlockId = document.Pages
            .SelectMany(static page => page.Blocks)
            .ToDictionary(
                static block => block.BlockId,
                static block => block.Text.Retrieval,
                StringComparer.Ordinal);
        var ordered = document.SectionTree
            .OrderBy(static section => section.Ordinal)
            .ToArray();
        var sections = new List<ExtractedDocumentSection>(ordered.Length);

        for (var index = 0; index < ordered.Length; index++)
        {
            var source = ordered[index];
            var title = string.Join(
                " ",
                source.TitleBlockIds
                    .Select(blockId => titleByBlockId.TryGetValue(
                        blockId,
                        out var value)
                        ? value
                        : string.Empty)
                    .Where(static value =>
                        !string.IsNullOrWhiteSpace(value)))
                .Trim();
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var nextBoundary = ordered
                .Skip(index + 1)
                .FirstOrDefault(candidate =>
                    candidate.Level <= source.Level);
            var pageEnd = nextBoundary is null
                ? document.PageCount
                : nextBoundary.PageStart > source.PageStart
                    ? nextBoundary.PageStart - 1
                    : source.PageStart;

            sections.Add(new(
                Ordinal: source.Ordinal,
                Title: title,
                Level: source.Level,
                PageStart: source.PageStart,
                PageEnd: Math.Max(source.PageStart, pageEnd),
                StartLine: null,
                EndLine: null));
        }

        return sections;
    }
}
