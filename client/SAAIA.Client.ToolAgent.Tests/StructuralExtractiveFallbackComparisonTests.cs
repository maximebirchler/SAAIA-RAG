using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class StructuralExtractiveFallbackComparisonTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Extractive_fallback_is_compared_with_honest_insufficiency_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_EXTRACTIVE_FALLBACK_COMPARISON"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_EXTRACTIVE_FALLBACK_COMPARISON to compare canonical excerpts with honest insufficiency.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_EXTRACTIVE_FALLBACK_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_EXTRACTIVE_FALLBACK_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_EXTRACTIVE_FALLBACK_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_EXTRACTIVE_FALLBACK_OUTPUT_ARTIFACT"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);

        using var input = JsonDocument.Parse(
            await File.ReadAllTextAsync(inputArtifact));
        var root = input.RootElement;
        var assignments = ReadAssignments(root);
        var entries = ReadEntries(root);
        Assert.InRange(assignments.Length, 2, 10);
        Assert.NotEmpty(entries);
        var byId = entries.ToDictionary(
            static entry => entry.EvidenceId,
            StringComparer.OrdinalIgnoreCase);
        Assert.All(assignments, assignment => Assert.All(
            assignment.EvidenceIds,
            id => Assert.Contains(id, byId.Keys)));
        Assert.All(entries, static entry => Assert.True(
            entry.HasCompleteCanonicalIdentity,
            "Incomplete canonical identity for " + entry.EvidenceId));

        const string warning =
            "Je n'ai pas pu vérifier une synthèse suffisamment fiable. "
            + "Voici uniquement les extraits canoniques sélectionnés, regroupés par facette : "
            + "ce ne sont ni une synthèse ni des conclusions.";
        var rendered = new StringBuilder();
        rendered.AppendLine(warning);
        var renderedFacets = new List<RenderedFacet>(assignments.Length);
        foreach (var assignment in assignments)
        {
            rendered.AppendLine();
            rendered.Append("Facette — ").AppendLine(assignment.Facet);
            var renderedExcerpts = new List<RenderedExcerpt>(
                assignment.EvidenceIds.Length);
            foreach (var evidenceId in assignment.EvidenceIds)
            {
                var entry = byId[evidenceId];
                var normalizedOriginal = NormalizeSpaces(entry.Text);
                var excerpt = CompactExactExcerpt(
                    normalizedOriginal,
                    maxCharacters: 600);
                var comparableExcerpt = excerpt.EndsWith('…')
                    ? excerpt[..^1].TrimEnd()
                    : excerpt;
                Assert.Contains(
                    comparableExcerpt,
                    normalizedOriginal,
                    StringComparison.Ordinal);
                rendered.Append("- « ").Append(excerpt).Append(" » [")
                    .Append(entry.EvidenceId).Append(", p.")
                    .Append(entry.PageStart).AppendLine("]");
                renderedExcerpts.Add(new RenderedExcerpt(
                    entry.EvidenceId,
                    entry.PageStart,
                    excerpt,
                    normalizedOriginal.Length,
                    excerpt.EndsWith('…')));
            }

            renderedFacets.Add(new RenderedFacet(
                assignment.FacetId,
                assignment.Facet,
                renderedExcerpts.ToArray()));
        }

        var extractiveCandidate = rendered.ToString().Trim();
        const string insufficiencyCandidate =
            "Je ne peux pas produire une synthèse suffisamment fiable à partir des preuves validées disponibles.";
        Assert.Contains("uniquement les extraits canoniques", extractiveCandidate);
        Assert.Contains("ni une synthèse ni des conclusions", extractiveCandidate);
        Assert.Equal(
            assignments.Length,
            Regex.Matches(extractiveCandidate, @"(?m)^Facette — ").Count);
        Assert.Equal(
            assignments.Sum(static assignment => assignment.EvidenceIds.Length),
            Regex.Matches(extractiveCandidate, @"(?m)^- « ").Count);

        var report = new
        {
            probe = "documents.structural_evidence.extractive_fallback_comparison",
            capturedAtUtc = DateTimeOffset.UtcNow,
            stage = "offline_candidates_rendered_and_mechanically_validated",
            inputArtifact,
            limits = new
            {
                maximumEvidencePerFacet = 2,
                maximumCharactersPerExcerpt = 600,
                modelUsed = false,
                productChanged = false,
                interactiveComputerControlUsed = false
            },
            metrics = new
            {
                facetCount = assignments.Length,
                evidenceCount = entries.Length,
                renderedEvidenceCount = renderedFacets.Sum(static facet =>
                    facet.Excerpts.Length),
                completeIdentityCount = entries.Count(static entry =>
                    entry.HasCompleteCanonicalIdentity),
                extractiveCharacters = extractiveCandidate.Length,
                insufficiencyCharacters = insufficiencyCandidate.Length,
                warningPresent = true,
                generatedDocumentaryClaimCount = 0
            },
            extractiveCandidate,
            insufficiencyCandidate,
            renderedFacets,
            assignments,
            evidence = entries
        };
        await File.WriteAllTextAsync(
            outputArtifact,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("facets=" + assignments.Length);
        output.WriteLine("evidence=" + entries.Length);
        output.WriteLine("extractive_characters=" + extractiveCandidate.Length);
        output.WriteLine("insufficiency_characters=" + insufficiencyCandidate.Length);
        output.WriteLine("extractive_candidate=" + extractiveCandidate);
        output.WriteLine("artifact=" + outputArtifact);
    }

    private static FacetAssignment[] ReadAssignments(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "selector", out var selector)
            || !TryGetPropertyIgnoreCase(
                selector,
                "facetSelections",
                out var selections)
            || selections.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return selections.EnumerateArray()
            .Select(static item => new FacetAssignment(
                ReadString(item, "FacetId"),
                ReadString(item, "Facet"),
                ReadStringArray(item, "EvidenceIds")))
            .ToArray();
    }

    private static EvidenceEntry[] ReadEntries(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return entries.EnumerateArray()
            .Select(static item => new EvidenceEntry(
                ReadString(item, "EvidenceId"),
                ReadString(item, "DocId"),
                ReadString(item, "RevisionId"),
                ReadString(item, "SourceHash"),
                ReadString(item, "DocPath"),
                ReadString(item, "DocName"),
                ReadString(item, "ChunkId"),
                ReadString(item, "AnchorId"),
                ReadInt(item, "PageStart"),
                ReadInt(item, "PageEnd"),
                ReadString(item, "Text")))
            .ToArray();
    }

    private static string CompactExactExcerpt(
        string normalizedText,
        int maxCharacters)
    {
        if (normalizedText.Length <= maxCharacters)
            return normalizedText;

        var window = normalizedText[..maxCharacters];
        var boundary = window.LastIndexOfAny(['.', '!', '?', ';', ':']);
        var take = boundary >= maxCharacters / 2
            ? boundary + 1
            : maxCharacters;
        return window[..take].TrimEnd() + "…";
    }

    private static string NormalizeSpaces(string value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string ReadString(JsonElement value, string propertyName)
        => TryGetPropertyIgnoreCase(value, propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement value, string propertyName)
        => TryGetPropertyIgnoreCase(value, propertyName, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static string[] ReadStringArray(
        JsonElement value,
        string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(value, propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim() ?? string.Empty)
            .Where(static item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement value,
        string propertyName,
        out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in value.EnumerateObject())
            {
                if (string.Equals(
                        candidate.Name,
                        propertyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static string Require(string name, string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("Missing " + name + ".")
            : value.Trim();

    private sealed record FacetAssignment(
        string FacetId,
        string Facet,
        string[] EvidenceIds);

    private sealed record EvidenceEntry(
        string EvidenceId,
        string DocId,
        string RevisionId,
        string SourceHash,
        string DocPath,
        string DocName,
        string ChunkId,
        string AnchorId,
        int PageStart,
        int PageEnd,
        string Text)
    {
        public bool HasCompleteCanonicalIdentity =>
            !string.IsNullOrWhiteSpace(EvidenceId)
            && !string.IsNullOrWhiteSpace(DocId)
            && !string.IsNullOrWhiteSpace(RevisionId)
            && SourceHash.Length == 64
            && !string.IsNullOrWhiteSpace(DocPath)
            && !string.IsNullOrWhiteSpace(ChunkId)
            && !string.IsNullOrWhiteSpace(AnchorId)
            && PageStart > 0
            && PageEnd >= PageStart
            && !string.IsNullOrWhiteSpace(Text);
    }

    private sealed record RenderedExcerpt(
        string EvidenceId,
        int Page,
        string Text,
        int OriginalCharacters,
        bool Truncated);

    private sealed record RenderedFacet(
        string FacetId,
        string Facet,
        RenderedExcerpt[] Excerpts);
}
