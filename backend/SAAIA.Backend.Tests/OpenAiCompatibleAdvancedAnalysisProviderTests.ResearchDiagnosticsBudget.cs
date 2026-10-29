using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task ResearchDiagnosticsBudget_eight_searches_keep_operational_feedback_without_phase_telemetry_or_mutation(
        string protocol)
    {
        var queries = Enumerable.Range(0, 8).Select(index => new { query = $"documented topic {index}", topK = 20 }).ToArray();
        const string insufficient = """{"outcome":"insufficient_documentation","answerText":"Le contexte actuel ne suffit pas.","claims":[]}""";
        using var factory = new QueuedHttpClientFactory(Completion(JsonSerializer.Serialize(new { selectionMode = "repeatable_named_items", queries })),
            protocol == "responses" ? NativeResponseAnswer(insufficient) : Completion(insufficient));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.NativeResearchToolsEnabled = true;
        options.NativeResearchTopology = "agent";
        options.NativeResearchApiProtocol = protocol;
        options.ExternalMaximumCallsPerJob = 2;
        var diagnostic = NinePhaseDiagnostic();
        var original = JsonSerializer.Serialize(diagnostic);
        var evidence = BuildEvidence("E1", "OptionA : contenu documentaire.");
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, [evidence], ["dense"], 1, number,
            RetrievalDiagnostic: diagnostic, DocumentResolution: new("resolved", 1, "Documents/known.pdf")));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Equal(8, gateway.Searches.Count);
        var user = WorkspaceUser(factory.Requests[1].Body);
        var tools = user.GetProperty("researchTools");
        Assert.False(tools.GetProperty("researchAllowed").GetBoolean());
        Assert.True(tools.TryGetProperty("documentaryBudget", out _));
        var prior = tools.GetProperty("priorSearches");
        Assert.Equal(8, prior.GetArrayLength());
        var diagnosticCharacters = 0;
        foreach (var search in prior.EnumerateArray())
        {
            var feedback = search.GetProperty("retrievalDiagnostic");
            diagnosticCharacters += feedback.GetRawText().Length;
            Assert.False(feedback.TryGetProperty("phases", out _));
            Assert.Equal(20, feedback.GetProperty("returnedEvidenceCandidates").GetInt32());
            Assert.Equal(9, feedback.GetProperty("omittedPhases").GetInt32());
            Assert.Equal("top_k_limit", Assert.Single(feedback.GetProperty("rejectionCounts").EnumerateArray()).GetProperty("reasonCode").GetString());
            Assert.Equal(60, feedback.GetProperty("rejectionCounts")[0].GetProperty("count").GetInt32());
            Assert.Contains("not distinct supported items", feedback.GetProperty("instruction").GetString());
            Assert.Contains("not exhaustive corpus coverage", feedback.GetProperty("instruction").GetString());
            Assert.Equal("resolved", search.GetProperty("documentResolution").GetProperty("status").GetString());
            Assert.Equal("Documents/known.pdf", search.GetProperty("documentResolution").GetProperty("resolvedDocPath").GetString());
            Assert.Equal("dense", search.GetProperty("degradedRetrievers")[0].GetString());
        }
        // The former nine-phase shape costs over 9,000 characters for eight
        // searches. This bound applies to actual captured request JSON, rather
        // than estimating tokens or changing the provider/corpus limits.
        Assert.True(diagnosticCharacters < 3_500, $"diagnosticCharacters={diagnosticCharacters}");
        Assert.DoesNotContain("durationMs", tools.GetRawText());
        Assert.DoesNotContain("canonical_dense_raw", tools.GetRawText());
        Assert.DoesNotContain("CONTENT_ONLY_DO_NOT_LEAK", tools.GetRawText());
        Assert.DoesNotContain("RAW_DIAGNOSTIC_QUERY", tools.GetRawText());
        Assert.Equal(original, JsonSerializer.Serialize(diagnostic));
        Assert.Equal(9, diagnostic.Phases.Count);
        Assert.Equal(176, diagnostic.Phases[2].DurationMs);
        Assert.Equal(80, diagnostic.Phases[2].Returned);
        Assert.Equal(20, diagnostic.Selection.Returned);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task ResearchDiagnosticsBudget_native_result_retains_bounded_failures_and_omission_counts(string protocol)
    {
        const string arguments = """{"query":"alternate","sourceKey":"","category":"","documentHint":"","topK":20}""";
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("search_corpus", arguments) : NativeCompletion(("search_corpus", arguments)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.NativeResearchToolsEnabled = true;
        options.NativeResearchTopology = "agent";
        options.NativeResearchApiProtocol = protocol;
        options.ExternalMaximumCallsPerJob = 4;
        var diagnostic = NinePhaseDiagnostic() with { DroppedPhases = 7, DroppedRejections = 3 };
        var original = JsonSerializer.Serialize(diagnostic);
        var evidence = BuildEvidence("E1", "Le seuil est 17 bar.");
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, number == 1 ? [evidence] : [], ["dense"], 1, number,
            RetrievalDiagnostic: diagnostic, DocumentResolution: new("no_match", 0, null)));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        using var body = JsonDocument.Parse(factory.Requests[2].Body);
        var messages = body.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray();
        var tool = messages.Single(message => protocol == "responses"
            ? message.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            : message.GetProperty("role").GetString() == "tool");
        using var output = JsonDocument.Parse(tool.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        var feedback = output.RootElement.GetProperty("retrievalDiagnostic");
        Assert.False(feedback.TryGetProperty("phases", out _));
        Assert.Equal(16, feedback.GetProperty("omittedPhases").GetInt32());
        Assert.Equal(3, feedback.GetProperty("droppedRejections").GetInt32());
        Assert.Equal(60, Assert.Single(feedback.GetProperty("rejectionCounts").EnumerateArray()).GetProperty("count").GetInt32());
        Assert.Contains("not exhaustive corpus coverage", feedback.GetProperty("instruction").GetString());
        Assert.Equal("no_match", output.RootElement.GetProperty("documentResolution").GetProperty("status").GetString());
        Assert.Equal("dense", output.RootElement.GetProperty("degradedRetrievers")[0].GetString());
        Assert.Equal(original, JsonSerializer.Serialize(diagnostic));
    }

    private static RagRetrievalDiagnosticsDto NinePhaseDiagnostic()
    {
        var diagnostic = FeedbackRetrievalDiagnostic();
        return diagnostic with
        {
            TopK = 20,
            Phases = [
                new("canonical_exact_raw", "exact_match", 0, 31, []),
                new("canonical_sparse_raw", "sparse_bm25", 0, 18, []),
                new("canonical_dense_raw", "dense_qdrant", 80, 176, [], 68),
                new("canonical_exact", "exact_match", 0, 31, []),
                new("canonical_sparse", "sparse_bm25", 0, 18, []),
                new("canonical_dense", "dense_qdrant", 80, 176, [], 68),
                new("canonical_fusion", "rrf", 80, 10, [], 68),
                new("canonical_rerank_skipped", "tei_rerank", 80, 0, [], 68),
                new("canonical_selected", "mechanical_selection", 20, 249, [], 8)],
            Selection = diagnostic.Selection with { Returned = 20, TopK = 20 },
            RejectionCounts = new Dictionary<string, int> { ["top_k_limit"] = 60 }
        };
    }
}
