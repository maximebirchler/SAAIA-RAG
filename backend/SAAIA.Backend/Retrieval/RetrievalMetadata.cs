using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalRows;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalMetadata
{

    internal sealed record CapabilityAHypQuestionDocRow(
        Guid DocId,
        string DocPath,
        string DocName,
        int IndexedVersion,
        string? HypotheticalQuestionsJson);


    internal sealed record CapabilityAHypQuestionDoc(
        Guid DocId,
        string DocPath,
        string DocName,
        int IndexedVersion,
        string[] HypotheticalQuestions);


    internal static string[] ParseJsonStringArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch
        {
            return [];
        }
    }


    internal static string BuildCategoryRef(int displayOrder)
        => $"cat_{displayOrder:000}";


    internal static string? ExtractTopLevelCategoryPath(string? categoryOrDocPath)
    {
        if (string.IsNullOrWhiteSpace(categoryOrDocPath))
            return null;

        var normalized = categoryOrDocPath.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }


    internal static async Task<List<RagMatch>> SearchDocumentMetadataMatchesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        IReadOnlyList<string> normalizedTerms,
        IReadOnlyList<string> referenceKeys,
        string? category,
        Guid? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null)
    {
        if (topK <= 0)
            return [];

        const string sql = """
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc"
FROM documents d
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY d.updated_at DESC
LIMIT 500;
""";

        var rows = await conn.QueryAsync<MetadataReferenceRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            category,
            category_path = NormalizeRagCategoryPathForSql(categoryPath),
            doc_id = docId,
            doc_path = docPath
        }, cancellationToken: ct));

        return rows
            .Select(row => BuildMetadataReferenceMatch(row, normalizedTerms, referenceKeys))
            .Where(static match => match is not null)
            .Select(static match => match!)
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToList();
    }


    private static RagMatch? BuildMetadataReferenceMatch(
        MetadataReferenceRow row,
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> queryReferenceKeys)
    {
        var metadataText = $"{row.DocName} {row.DocPath}";
        var metadataTerms = ExactMatchEntryExtractor.ExtractLookupTerms(metadataText);
        var metadataKeys = ExactMatchEntryExtractor.ExtractReferenceKeys(metadataText);
        var metadataReferenceTerms = ExactMatchEntryExtractor.ExtractTargetedReferences(metadataText)
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(static term => IsReferenceLikeLookupTerm(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var queryReferenceTerms = queryTerms
            .Where(static term => IsReferenceLikeLookupTerm(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var exactReferenceMatches = queryReferenceTerms
            .Intersect(metadataReferenceTerms, StringComparer.Ordinal)
            .ToArray();
        var keyBackedReferenceMatches = metadataReferenceTerms
            .Where(term => QueryKeysFullyMatchTerm(queryReferenceKeys, term))
            .ToArray();
        var genericDirectMatches = queryTerms
            .Intersect(metadataTerms, StringComparer.Ordinal)
            .Except(exactReferenceMatches, StringComparer.Ordinal)
            .ToArray();
        var keyMatches = queryReferenceKeys
            .Intersect(metadataKeys, StringComparer.Ordinal)
            .ToArray();

        if (exactReferenceMatches.Length == 0
            && keyBackedReferenceMatches.Length == 0
            && genericDirectMatches.Length == 0
            && keyMatches.Length == 0)
            return null;

        var score = ComputeMetadataReferenceScore(
            exactReferenceMatches.Length,
            keyBackedReferenceMatches.Length,
            genericDirectMatches.Length,
            keyMatches.Length);
        var strongestReference = exactReferenceMatches.FirstOrDefault()
            ?? keyBackedReferenceMatches.FirstOrDefault();
        var text = strongestReference is not null
            ? $"{row.DocName} [{strongestReference}]"
            : keyMatches.Length > 0
                ? $"{row.DocName} [{string.Join(", ", keyMatches)}]"
                : row.DocName;

        return new RagMatch(
            Score: score,
            DocId: row.DocId.ToString(),
            DocPath: row.DocPath,
            DocName: row.DocName,
            Category: row.Category,
            PageStart: null,
            PageEnd: null,
            ChunkId: $"docmeta:{row.DocId}",
            ChunkIndex: -1,
            Text: text,
            IngestionVersion: row.IngestionVersion,
            HashDoc: row.HashDoc,
            EmbedText: text,
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: null,
            HeadingPath: null,
            ChunkType: "document_metadata_ref",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);
    }


    internal static double ComputeMetadataReferenceScore(int exactReferenceMatches, int keyBackedReferenceMatches, int genericDirectMatches, int keyMatches)
    {
        var score = 0.90;
        if (exactReferenceMatches > 0)
            score += 0.09 + Math.Min(0.01, exactReferenceMatches * 0.004);
        else if (keyBackedReferenceMatches > 0)
            score += 0.07 + Math.Min(0.01, keyBackedReferenceMatches * 0.004);
        else if (keyMatches > 0)
            score += 0.05 + Math.Min(0.01, keyMatches * 0.004);

        if (genericDirectMatches > 0)
            score += Math.Min(0.01, genericDirectMatches * 0.0025);

        return Math.Min(1.01, score);
    }


    private static bool QueryKeysFullyMatchTerm(IReadOnlyList<string> queryReferenceKeys, string metadataReferenceTerm)
    {
        if (queryReferenceKeys.Count == 0 || string.IsNullOrWhiteSpace(metadataReferenceTerm))
            return false;

        return queryReferenceKeys.All(key =>
            metadataReferenceTerm.Contains(key, StringComparison.Ordinal));
    }


    internal static bool IsReferenceLikeLookupTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return false;

        return term.Length >= 2 && term.Any(char.IsDigit) && term.Any(char.IsLetter);
    }


    internal static bool HasReferenceLikeQueryToken(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        if (ExtractLexicalQueryTokens(query).Any(IsReferenceLikeLookupTerm)
            || ExtractLexicalQuerySurfaceTokens(query).Any(IsReferenceLikeLookupTerm))
        {
            return true;
        }

        return Regex.IsMatch(
            query,
            @"\b[\p{L}]{1,12}[-_][\p{L}\p{Nd}]*\d[\p{L}\p{Nd}]*\b|\b[A-Z]{1,8}\d{1,6}[A-Z0-9]*\b|\b[A-Z]{1,8}\s+\d{1,6}[A-Z0-9]*\b|\b\d{1,6}[A-Z]{1,8}(?:[-_\s]+\d{1,8}[A-Z0-9]*)?\b",
            RegexOptions.CultureInvariant);
    }

}
