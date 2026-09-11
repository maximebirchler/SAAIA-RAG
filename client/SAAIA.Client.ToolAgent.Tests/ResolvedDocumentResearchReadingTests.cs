using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ResolvedDocumentResearchReadingTests
{
    private const string DocId = "4428c9b2-01d0-4eb5-b12b-c3af86d9b131";
    private const string OtherDocId = "549083d1-fb8a-4273-a4c6-d6c8983e9ffc";

    [Theory]
    [InlineData("resolved", true, false)]
    [InlineData("ambiguous", false, false)]
    [InlineData("incomplete", false, false)]
    [InlineData("alternative", false, false)]
    [InlineData("multiple", false, false)]
    [InlineData("missing", false, false)]
    [InlineData("invalid-id", false, false)]
    [InlineData("resolved", true, true)]
    public async Task Catalog_identity_can_offer_reading_without_becoming_evidence(
        string state, bool offered, bool wrongIdentity)
    {
        // Catalog identities are deliberately synthetic. The empty degraded search
        // is the actual HTTP contract captured in A701, never source content.
        var candidate = new SourceBackedDocumentResolutionCandidate(
            state == "invalid-id" ? "not-a-document-id" : DocId,
            "Lab/Inspection-log.pdf", "Inspection-log.pdf");
        var intake = new SourceBackedIntake("Read the recorded values and conditions.",
            "rag.answer", [], [], false, "en")
        {
            DocumentScope = state == "alternative" ? SourceBackedDocumentScope.AlternativeSources
                : SourceBackedDocumentScope.RequestedDocument,
            RequestedDocumentResolution = state == "missing" ? null : new(
                candidate.DocName, state == "ambiguous" ? SourceBackedDocumentResolutionStatus.Ambiguous
                    : SourceBackedDocumentResolutionStatus.Resolved,
                state != "incomplete", state == "multiple" ? [candidate, candidate with { DocId = OtherDocId }] : [candidate],
                "test_catalog_observation", 1, 1, false, 1),
            InitialToolCalls = [new("initial", "rag_search",
                JsonSerializer.SerializeToElement(new { query = "recorded values", docId = DocId, topK = 10 }), "llm_router")],
            InitialSemanticMission = new(JsonSerializer.SerializeToElement(new
            {
                planKind = "multi_item",
                deliverable = "recorded values and their conditions",
                structuredLayout = false,
                rowCount = 1,
                columnCount = 1,
                atomicEvidenceCount = 3,
                atomicEvidenceType = "documented facts",
                atomicEvidenceMode = "content_claim",
                selectionPolicy = "explicit_set",
                initialCapability = "rag_search",
                rowHeader = "",
                rowLabels = Array.Empty<string>(),
                columns = Array.Empty<string>()
            }), "llm_router")
        };
        if (!offered)
        {
            // Exercise the availability contract directly: unresolved identities
            // may otherwise terminate during the runner's earlier catalog gate.
            var tools = (IReadOnlyList<SourceBackedAgentToolDefinition>)typeof(SourceBackedAgentV2Runner)
                .GetMethod("AddResolvedDocumentReadingTool", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, [Array.Empty<SourceBackedAgentToolDefinition>(), intake])!;
            Assert.DoesNotContain(tools, tool => tool.Name == "documents_context");
            return;
        }
        var llm = new ReadingLlm(offered, wrongIdentity);
        var executor = new RecordingExecutor();
        var options = new SourceBackedAgentV2Options(
            MaximumTurns: 3, MaximumToolCalls: 2, MaximumObservationItems: 8,
            MaximumObservationExcerptCharacters: 360, MaximumOutputTokens: 900,
            MaximumActionTokens: 256, MaximumWorkingEvidenceItems: 12,
            MaximumSemanticCorrectionTurns: 0, SemanticCandidateAuditEnabled: false,
            SemanticColumnRoleReviewEnabled: false, MaximumSelectionProtocolRepairTurns: 0,
            StructuredSemanticPlanningEnabled: false, RequireEvidenceSelectionBeforeWriter: true,
            SemanticCandidateStrategyEnabled: false);
        var result = await new SourceBackedAgentV2Runner(llm, executor, options)
            .RunAsync(intake, CancellationToken.None);

        Assert.True(llm.ResearchDecisions > 0);
        var context = executor.Calls.Where(call => call.Name == "documents.context").ToArray();
        if (offered && !wrongIdentity)
        {
            var read = Assert.Single(context);
            Assert.Equal(DocId, read.Arguments.GetProperty("docId").GetString());
            Assert.Equal(7, read.Arguments.GetProperty("pageStart").GetInt32());
            Assert.Equal(8, read.Arguments.GetProperty("pageEnd").GetInt32());
            Assert.Equal(2, read.Arguments.GetProperty("limit").GetInt32());
            Assert.False(read.Arguments.TryGetProperty("evidenceId", out _));
        }
        else Assert.Empty(context);
        Assert.False(result.IsSourceVerified);
        if (wrongIdentity)
            Assert.Contains(result.TraceEvents, trace => trace.EventName == "source_backed_agent_v2.tool.rejected");
    }

    private sealed class RecordingExecutor : ISourceBackedAgentToolExecutor
    {
        public List<(string Name, JsonElement Arguments)> Calls { get; } = [];
        public Task<ToolResults> ExecuteToolCallAsync(SourceBackedIntake intake, string toolName,
            JsonElement arguments, CancellationToken ct)
        {
            Calls.Add((toolName, arguments.Clone()));
            var results = new ToolResults();
            if (Calls.Count == 1)
                results.Items.Add(new ToolResults.Item
                {
                    ToolName = "rag.search",
                    Result =
                    ToolAgentOrchestrator.NormalizeRagHitsForTests(File.ReadAllText(Path.Combine(
                        AppContext.BaseDirectory, "Fixtures", "CanonicalDegradedSearchContract.json")), sourceBackedCanonical: true)
                });
            return Task.FromResult(results);
        }
    }

    private sealed class ReadingLlm(bool offered, bool wrongIdentity) : ISourceBackedAgentLlmClient
    {
        public int ResearchDecisions { get; private set; }
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false)
        {
            SourceBackedAgentToolCall call;
            if (tools.Any(tool => tool.Name == "start_document_search"))
            {
                ResearchDecisions++;
                var reading = tools.SingleOrDefault(tool => tool.Name == "documents_context");
                Assert.Equal(offered, reading is not null);
                if (reading is not null)
                {
                    var properties = reading.Parameters.GetProperty("properties");
                    Assert.Equal(DocId, Assert.Single(properties.GetProperty("docId").GetProperty("enum").EnumerateArray()).GetString());
                    Assert.False(properties.TryGetProperty("evidenceId", out _));
                    Assert.False(properties.TryGetProperty("chunkId", out _));
                    Assert.Contains("Inspection-log.pdf", reading.Description);
                    call = new("model-read", "documents_context", JsonSerializer.SerializeToElement(new
                    { docId = wrongIdentity ? OtherDocId : DocId, pageStart = 7, pageEnd = 8, limit = 2 }));
                }
                else call = new("model-search", "start_document_search",
                    JsonSerializer.SerializeToElement(new { query = "other observed terms", limit = 2 }));
            }
            else if (tools.Any(tool => tool.Name == "declare_source_insufficiency"))
            {
                call = new("model-stop", "declare_source_insufficiency", JsonSerializer.SerializeToElement(new
                { reason = "The inspected observations contain no citable passage." }));
            }
            else
            {
                Assert.Contains(tools, tool => tool.Name == "resolve_source_yield");
                call = new("model-stop", "resolve_source_yield", JsonSerializer.SerializeToElement(new
                { decision = "insufficiency", message = "The inspected observations contain no citable passage." }));
            }
            return Task.FromResult(new SourceBackedAgentCompletion("", [call], "tool_calls", 100, 30));
        }
    }
}
