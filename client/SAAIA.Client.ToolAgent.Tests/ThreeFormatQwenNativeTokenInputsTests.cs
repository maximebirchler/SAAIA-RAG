using System.Text;
using System.Text.Json;
using Xunit;
using static SAAIA.Client.ToolAgent.Tests.ThreeFormatQwenContractHarness;

namespace SAAIA.Client.ToolAgent.Tests;

// A658 preparation: builders only. This class cannot send HTTP or start a model.
public sealed class ThreeFormatQwenNativeTokenInputsTests
{
    [Fact]
    public void A658_exports_every_stage_with_frozen_schemas_and_oracle_free_messages()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RAG.sln"))) root = root.Parent;
        var phase = Path.Combine(root!.FullName, "artifacts", "goal-rag-product-20260827-1041", "phase5");
        var inputs = LoadFrozenInputs(Path.Combine(phase, "A656-MANIFESTE-CONTRATS-TROIS-FORMATS.json"),
            Path.Combine(phase, "A630-CASES-NORMALISES-ORACLES.json"), Path.Combine(phase, "A656-ORDRE-MICROCAMPAGNE-25-CAS.json"));
        var rows = new List<object>();
        var requests = new List<Request>();
        var answerCases = 0;
        foreach (var state in inputs.States)
        {
            AddRole(state, "controller", "controller", null);
            // Only the measurement schedule uses this classification; no oracle enters a model request.
            if (inputs.ScoringCases[state.CasePosition].GetProperty("expectedDecision").GetString() != "answer") continue;
            answerCases++;
            var representative = JsonSerializer.Serialize(new
            {
                presentation = "paragraph",
                claims = state.Evidence.Select(e =>
                new { text = string.Concat(e.Excerpt.EnumerateRunes().Take(500).Select(r => r.ToString())), evidenceIds = new[] { e.Id } }).ToArray()
            });
            var minimal = JsonSerializer.Serialize(new { presentation = "paragraph", claims = new[] { new { text = ".", evidenceIds = new[] { state.Evidence[0].Id } } } });
            AddRole(state, "reviewer", "representative", representative);
            AddRole(state, "reviewer", "minimal", minimal);
        }
        Assert.Equal(13, answerCases);
        Assert.Equal(510, rows.Count);
        Assert.Equal(250, requests.Count(r => r.Role == "controller"));
        Assert.Equal(260, requests.Count(r => r.Role == "reviewer"));
        Assert.All(requests, request =>
        {
            foreach (var key in new[] { "expectedDecision", "expectedEvidenceIds", "requiredPatterns", "isDangerous" })
                Assert.DoesNotContain(key, request.Body.GetRawText());
            Assert.Equal(request.Role == "controller" ? 512 : 256, request.Body.GetProperty("max_tokens").GetInt32());
            Assert.Equal(0, request.Body.GetProperty("temperature").GetInt32());
        });
        var output = Environment.GetEnvironmentVariable("SAAIA_A658_PREPARE_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                version = "a658-pc-native-inputs-v1",
                ContractHash,
                CasesHash,
                OrderHash,
                representativeDraftOrigin = "mechanical_frozen_excerpts_not_semantic_answer",
                reviewerMaximumProjection = "max(representativeTokens,minimalTokens+512)+32; same conservative replay envelope as A648",
                controllerInputLimit = 3392,
                reviewerInputLimit = 3648,
                context = 4096,
                rows
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        void AddRole(ModelState state, string role, string form, string? draft)
        {
            var verifier = role == "reviewer" ? "{\"valid\":true,\"errors\":[]}" : null;
            Add(BuildDirectRequest(inputs, state, role, draft, verifier));
            Add(BuildGrammarRequest(inputs, state, role, draft, verifier));
            Add(BuildRouteRequest(inputs, state, role, draft, verifier));
            foreach (var action in inputs.Contracts.GetProperty("customSchemas").GetProperty(role + "Route")
                         .GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray())
                Add(BuildSpecializedPayloadRequest(inputs, state, action.GetString()!, role, draft, verifier));

            void Add(Request request)
            {
                requests.Add(request);
                rows.Add(new
                {
                    sequence = rows.Count + 1,
                    state.CasePosition,
                    state.CaseId,
                    form,
                    request.Variant,
                    request.Role,
                    request.Stage,
                    request.SelectedAction,
                    request.SnapshotSha256,
                    request.SchemaSha256,
                    request.Body
                });
            }
        }
    }
}
