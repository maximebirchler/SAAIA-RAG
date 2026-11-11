using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", false)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", true)]
    public async Task Reference_repair_receives_the_exact_bad_location_without_guessing_a_replacement(
        string protocol, bool critic)
    {
        const string validId = "advanced-evidence-0123456789abcdef0123456789abcdef";
        var truncatedId = validId[..^1];
        string Answer(string id) => JsonSerializer.Serialize(new
        {
            outcome = "answered", answerText = "La limite documentée est 17 bar [C1].",
            claims = new[] { new { claimId = "C1", text = "La limite documentée est 17 bar.", evidenceIds = new[] { id, "E2" } } }
        });
        var good = Answer(validId); var bad = Answer(truncatedId);
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner),
            EarlyReply(protocol, critic ? good : bad), EarlyReply(protocol, critic ? bad : good), EarlyReply(protocol, good));
        var options = WorkspaceOptions(protocol); options.SemanticCriticEnabled = true;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(
                BuildEvidence(validId, "La limite documentée est 17 bar."), BuildEvidence("E2", "Procédure de contrôle du seuil.")), CancellationToken.None);

        Assert.Equal(4, factory.Requests.Count);
        var prompt = WorkspaceUser(factory.Requests[critic ? 3 : 2].Body);
        var diagnostic = prompt.GetProperty("evidenceReferenceDiagnostics");
        Assert.Equal(0, diagnostic.GetProperty("indexBase").GetInt32());
        Assert.Equal("current_visible_evidence", diagnostic.GetProperty("scope").GetString());
        var issue = Assert.Single(diagnostic.GetProperty("issues").EnumerateArray());
        Assert.Equal("claims[0].evidenceIds[0]", issue.GetProperty("path").GetString());
        Assert.Equal("id_not_in_current_evidence", issue.GetProperty("reason").GetString());
        Assert.Equal(truncatedId, issue.GetProperty("suppliedEvidenceId").GetString());
        Assert.DoesNotContain(validId, diagnostic.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(new[] { validId, "E2" }, Assert.Single(result.Claims).EvidenceIds);
    }

    [Fact]
    public void Reference_diagnostics_match_parser_normalization_and_do_not_invent_a_mapping()
    {
        const string raw = """{"claims":[{"evidenceIds":[" E1 ",null,3,"", "E1"]},{"evidenceIds":["e1"]},{"evidenceIds":[null,"  "]},{"evidenceIds":null},{}]}""";
        var result = OpenAiCompatibleAdvancedAnalysisProvider.DiagnoseEvidenceReferences(raw, ["E1"]);
        Assert.Equal("observed", result.Status);
        Assert.Equal(4, result.TotalIssueCount);
        Assert.Equal(new[] { "claims[1].evidenceIds[0]", "claims[2].evidenceIds", "claims[3].evidenceIds", "claims[4].evidenceIds" }, result.Issues.Select(i => i.Path));
        Assert.Equal("e1", result.Issues[0].SuppliedEvidenceId);
        Assert.Equal("no_usable_reference", result.Issues[1].Reason);
    }

    [Fact]
    public void Reference_diagnostics_bound_untrusted_values_and_issue_count_without_splitting_a_surrogate()
    {
        var id = new string('x', 159) + "😀suffix";
        var raw = JsonSerializer.Serialize(new { claims = Enumerable.Range(0, 70).Select(_ => new { evidenceIds = new[] { id } }) });
        var result = OpenAiCompatibleAdvancedAnalysisProvider.DiagnoseEvidenceReferences(raw, ["E1"]);
        Assert.Equal(70, result.TotalIssueCount); Assert.Equal(6, result.OmittedIssueCount);
        Assert.Equal(64, result.Issues.Count);
        Assert.All(result.Issues, issue =>
        {
            Assert.Equal(new string('x', 159), issue.SuppliedEvidenceId);
            Assert.Equal(id.Length, issue.SuppliedIdCharacterCount);
        });
    }

    [Theory]
    [InlineData("{", "invalid_json")]
    [InlineData("[]", "claims_unavailable")]
    [InlineData("{}", "claims_unavailable")]
    [InlineData("{\"claims\":null}", "claims_unavailable")]
    public void Reference_diagnostics_do_not_hide_malformed_protocol(string raw, string status)
    {
        var result = OpenAiCompatibleAdvancedAnalysisProvider.DiagnoseEvidenceReferences(raw, ["E1"]);
        Assert.Equal(status, result.Status); Assert.Empty(result.Issues);
    }
}
