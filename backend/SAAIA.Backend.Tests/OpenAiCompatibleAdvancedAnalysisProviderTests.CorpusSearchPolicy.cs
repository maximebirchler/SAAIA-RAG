using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("zero", "zero_hits")]
    [InlineData("positive", "has_hits")]
    [InlineData("missing", "unknown")]
    [InlineData("omitted", "unknown")]
    [InlineData("timeout", "unknown")]
    [InlineData("sparse_alias_degraded", "unknown")]
    [InlineData("duplicate", "unknown")]
    [InlineData("negative", "unknown")]
    [InlineData("unknown_duration", "unknown")]
    [InlineData("wrong_retriever", "unknown")]
    [InlineData("interactive", "unknown")]
    [InlineData("dense_degraded", "zero_hits")]
    public void CorpusSearchPolicy_sparse_status_distinguishes_lexical_zero_from_unknown_without_phase_leaks(
        string scenario, string expected)
    {
        var diagnostic = CorpusSearchDiagnostic();
        IReadOnlyList<string> degraded = [];
        var sparse = diagnostic.Phases[0];
        diagnostic = scenario switch
        {
            "positive" => diagnostic with { Phases = [sparse with { Returned = 3 }, diagnostic.Phases[1]] },
            "missing" => diagnostic with { Phases = [diagnostic.Phases[1]] },
            "omitted" => diagnostic with { DroppedPhases = 1 },
            "duplicate" => diagnostic with { Phases = [sparse, sparse, diagnostic.Phases[1]] },
            "negative" => diagnostic with { Phases = [sparse with { Returned = -1 }, diagnostic.Phases[1]] },
            "unknown_duration" => diagnostic with { Phases = [sparse with { DurationMs = null }, diagnostic.Phases[1]] },
            "wrong_retriever" => diagnostic with { Phases = [sparse with { Retriever = "dense_qdrant" }, diagnostic.Phases[1]] },
            "interactive" => diagnostic with { Mode = "balanced" },
            _ => diagnostic
        };
        if (scenario == "timeout") degraded = ["canonical_sparse_fts"];
        if (scenario == "sparse_alias_degraded") degraded = ["SPARSE_BM25"];
        if (scenario == "dense_degraded") degraded = ["dense_qdrant"];
        var before = JsonSerializer.Serialize(diagnostic);
        Assert.Contains("CONTENT_ONLY_DO_NOT_LEAK", before);

        // Reflection keeps this causal test compilable on the pre-fix baseline:
        // that version projects no sparse status, rather than failing to compile.
        var method = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod(
            "BuildRetrievalDiagnosticForPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;
        var feedback = JsonSerializer.SerializeToElement(method.Invoke(null,
            method.GetParameters().Length == 1 ? [diagnostic] : [diagnostic, degraded]));

        Assert.True(feedback.TryGetProperty("canonicalSparseStatus", out var status),
            "Hybrid feedback must distinguish lexical zero from an unknown sparse run.");
        Assert.Equal(expected, status.GetString());
        Assert.Equal(20, feedback.GetProperty("returnedEvidenceCandidates").GetInt32());
        Assert.False(feedback.TryGetProperty("phases", out _));
        Assert.DoesNotContain("CONTENT_ONLY_DO_NOT_LEAK", feedback.GetRawText());
        Assert.DoesNotContain("durationMs", feedback.GetRawText());
        Assert.Equal(before, JsonSerializer.Serialize(diagnostic));
    }

    [Theory]
    [InlineData(false, "chat-completions")]
    [InlineData(true, "chat-completions")]
    [InlineData(true, "responses")]
    public async Task CorpusSearchPolicy_real_native_and_legacy_requests_preserve_queries_scopes_and_user_obligations(
        bool native, string protocol)
    {
        const string initialQuery = "\"argon purge\" OR inertage";
        const string scopedQuery = "\"seuil de pression\" OR consigne";
        var planner = JsonSerializer.Serialize(new
        {
            queries = new[] { new { query = initialQuery, category = "Corpus", documentHint = "", topK = 13 } }
        });
        var scoped = new { query = scopedQuery, sourceKey = "internal-source-1", category = "Corpus", documentHint = "", topK = 17 };
        var research = native
            ? protocol == "responses"
                ? NativeResponseFunction("search_corpus", JsonSerializer.Serialize(scoped))
                : NativeCompletion(("search_corpus", JsonSerializer.Serialize(scoped)))
            : Completion(JsonSerializer.Serialize(new { outcome = "research_required", queries = new[] { scoped } }));
        using var factory = new QueuedHttpClientFactory(Completion(planner),
            Completion("""{"decision":"ready","queries":[]}"""), research,
            native && protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.NativeResearchToolsEnabled = native;
        options.NativeResearchApiProtocol = protocol;
        options.ExternalMaximumCallsPerJob = 5;
        var originalOptions = JsonSerializer.Serialize(options);
        var request = BuildDirectRequest();
        var originalHandoff = JsonSerializer.Serialize(request.Handoff);
        var evidence = BuildEvidence("E1", "Le seuil est 17 bar.", fileName: "inertage.pdf");
        var gateway = new FeedbackToolGateway((search, number) => new(search.Query, [evidence], [], 1, number,
            RetrievalDiagnostic: CorpusSearchDiagnostic()));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, factory.Requests.Count);
        Assert.Equal(2, gateway.Searches.Count);
        Assert.Equal(initialQuery, gateway.Searches[0].Query);
        Assert.Equal(scopedQuery, gateway.Searches[1].Query);
        Assert.Equal("Corpus", gateway.Searches[0].Category);
        Assert.Equal("Corpus", gateway.Searches[1].Category);
        Assert.Equal(13, gateway.Searches[0].TopK);
        Assert.Equal(17, gateway.Searches[1].TopK);
        Assert.Null(gateway.Searches[0].DocId);
        Assert.Equal(evidence.Reference.DocId, gateway.Searches[1].DocId);
        Assert.Equal(evidence.Reference.RevisionId, gateway.Searches[1].RevisionId);
        Assert.Equal(evidence.Reference.DocPath, gateway.Searches[1].DocPath);
        Assert.Null(gateway.Searches[1].DocumentHint);
        Assert.Equal(originalHandoff, JsonSerializer.Serialize(request.Handoff));
        Assert.Equal(originalOptions, JsonSerializer.Serialize(options));

        var plannerSystem = CorpusSearchSystem(factory.Requests[0].Body);
        var reviewSystem = CorpusSearchSystem(factory.Requests[1].Body);
        AssertCorpusSearchContract(plannerSystem);
        AssertCorpusSearchContract(reviewSystem);
        Assert.DoesNotContain("with useful\nsynonyms", plannerSystem);
        Assert.DoesNotContain("using concrete alternate item names", plannerSystem);
        var review = WorkspaceUser(factory.Requests[1].Body);
        Assert.Equal("zero_hits", review.GetProperty("priorSearches")[0]
            .GetProperty("retrievalDiagnostic").GetProperty("canonicalSparseStatus").GetString());
        Assert.Equal(20, review.GetProperty("priorSearches")[0]
            .GetProperty("retrievalDiagnostic").GetProperty("returnedEvidenceCandidates").GetInt32());
        var legacyTool = review.GetProperty("tools").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "search_corpus");
        AssertCorpusSearchContract(legacyTool.GetProperty("query").GetString()!);

        if (native)
        {
            using var envelope = JsonDocument.Parse(factory.Requests[2].Body);
            var declaration = envelope.RootElement.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.TryGetProperty("function", out var function) ? function : tool)
                .Single(tool => tool.GetProperty("name").GetString() == "search_corpus");
            AssertCorpusSearchContract(declaration.GetProperty("description").GetString()!);
            using var next = JsonDocument.Parse(factory.Requests[3].Body);
            var messages = next.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray();
            var tool = messages.Single(message => protocol == "responses"
                ? message.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : message.GetProperty("role").GetString() == "tool");
            using var output = JsonDocument.Parse(tool.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
            Assert.Equal("zero_hits", output.RootElement.GetProperty("retrievalDiagnostic")
                .GetProperty("canonicalSparseStatus").GetString());
            Assert.Equal(20, output.RootElement.GetProperty("retrievalDiagnostic")
                .GetProperty("returnedEvidenceCandidates").GetInt32());
        }
    }

    private static void AssertCorpusSearchContract(string text)
    {
        Assert.Contains("hybrid lexical and semantic", text);
        Assert.Contains("terms must occur together", text);
        Assert.Contains("Put variants in separate", text);
        Assert.Contains("mandatory qualifier", text);
        Assert.Contains("even if semantic candidates were returned", text);
        Assert.Contains("unknown is not zero", text);
    }

    private static string CorpusSearchSystem(string envelope)
    {
        using var document = JsonDocument.Parse(envelope);
        return document.RootElement.GetProperty(document.RootElement.TryGetProperty("input", out _) ? "input" : "messages")
            .EnumerateArray().Single(message => message.TryGetProperty("role", out var role)
                && role.GetString() is "system" or "developer").GetProperty("content").GetString()!;
    }

    private static RagRetrievalDiagnosticsDto CorpusSearchDiagnostic()
        => FeedbackRetrievalDiagnostic() with
        {
            Mode = "source_backed_canonical",
            TopK = 20,
            Phases = [
                new("canonical_sparse_raw", "sparse_bm25", 0, 5, []),
                new("canonical_dense_raw", "dense_qdrant", 80, 10,
                    FeedbackRetrievalDiagnostic().Phases[0].TopCandidates, 79)],
            Selection = FeedbackRetrievalDiagnostic().Selection with { Returned = 20, TopK = 20 }
        };
}
