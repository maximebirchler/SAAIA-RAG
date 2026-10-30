using SAAIA.Backend.Models;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const string CorpusSearchQueryPolicy = """
        search_corpus is hybrid lexical and semantic retrieval. In its lexical
        channel, ordinary whitespace-separated terms must occur together; a list
        of alternative names or synonyms is not an alternatives query. Use one
        concept or an exact observed title per query. Put variants in separate
        complementary queries within the existing limits. Quoted text requests
        a phrase; do not append every requested fact or layout label to it.
        Preserve the complete user goal and every mandatory qualifier in the
        handoff and final evidence checks; shorter retrieval wording does not
        relax them or change document and category scopes.
        canonicalSparseStatus=zero_hits means this lexical operation returned no
        candidates, even if semantic candidates were returned. Try a shorter
        distinctive concept or an observed title when the results do not address
        the need. has_hits is not proof of relevance; unknown is not zero.
        None of these statuses proves corpus absence or supported item counts.
        """;

    private static string BuildCanonicalSparseStatus(
        RagRetrievalDiagnosticsDto diagnostic,
        IReadOnlyList<string>? degradedRetrievers)
    {
        // The canonical SQL catches database errors/timeouts and returns an
        // empty list while separately marking canonical_sparse_fts degraded.
        // A phase with zero rows alone therefore cannot establish zero hits.
        if (diagnostic.Mode != "source_backed_canonical" || diagnostic.DroppedPhases != 0
            || degradedRetrievers?.Any(static name =>
                string.Equals(name.Trim(), "canonical_sparse_fts", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name.Trim(), "sparse_bm25", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name.Trim(), "sparse", StringComparison.OrdinalIgnoreCase)) == true)
            return "unknown";

        var phases = diagnostic.Phases.Where(static phase => phase.Name == "canonical_sparse_raw").ToArray();
        if (phases.Length != 1 || phases[0].Retriever != "sparse_bm25"
            || phases[0].Returned < 0 || phases[0].DurationMs is not >= 0)
            return "unknown";

        return phases[0].Returned == 0 ? "zero_hits" : "has_hits";
    }
}
