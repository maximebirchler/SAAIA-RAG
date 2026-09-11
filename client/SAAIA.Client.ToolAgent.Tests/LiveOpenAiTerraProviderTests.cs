using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveOpenAiTerraProviderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Terra_supports_strict_router_json_and_streamed_writer_when_explicitly_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_RUN_OPENAI_TERRA_PROVIDER_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_RUN_OPENAI_TERRA_PROVIDER_TEST=1 for an explicitly paid Terra probe.");
            return;
        }

        var configuration = LlmProviderConfiguration.Load();
        Assert.Equal(LlmProviderMode.OpenAiDev, configuration.Mode);
        var provider = LlmProviderFactory.Create(
            new OpenAiLlmClient(),
            new AppSettings(),
            configuration);
        provider.ConfigureGeneration(0.2, 512);
        var metrics = new List<LlmCallMetrics>();
        provider.CallCompleted += metrics.Add;
        var contract = LlmStructuredOutputContract.Parse(
            "saaia_terra_smoke_router",
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "intent": { "type": "string", "enum": ["direct_answer"] },
                "language": { "type": "string", "enum": ["fr"] },
                "needClarification": { "type": "boolean", "const": false }
              },
              "required": ["intent", "language", "needClarification"]
            }
            """);
        string routerJson;
        SourceBackedAgentCompletion nativeRouter;
        var streamed = new StringBuilder();
        using (provider.BeginTurn("terra-provider-live-smoke"))
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
        {
            routerJson = await provider.CompleteStructuredAsync(
                new[]
                {
                    (role: "system", content: "Tu es un routeur SAAIA. Classe la demande selon le schéma."),
                    (role: "user", content: "Réponds directement en français.")
                },
                contract,
                cts.Token);
            nativeRouter = await provider.CompleteAsync(
                new[]
                {
                    SourceBackedAgentMessage.System(
                        "Tu es le Router SAAIA. Appelle obligatoirement route_direct_answer."),
                    SourceBackedAgentMessage.User("Réponds directement en français.")
                },
                new[]
                {
                    new SourceBackedAgentToolDefinition(
                        "route_direct_answer",
                        "Sélectionne la route de réponse directe.",
                        JsonSerializer.SerializeToElement(new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                language = new { type = "string", @enum = new[] { "fr" } }
                            },
                            required = new[] { "language" }
                        }))
                },
                maxTokens: 512,
                cts.Token,
                requireToolCall: true);
            await provider.StreamAsync(
                new[]
                {
                    (role: "system", content: "Tu es le Writer SAAIA. Réponds en une phrase courte."),
                    (role: "user", content: "Dis que le test de streaming Terra fonctionne, en français.")
                },
                forceJson: false,
                chunk => streamed.Append(chunk),
                cts.Token);
        }

        using var router = JsonDocument.Parse(routerJson);
        Assert.Equal("direct_answer", router.RootElement.GetProperty("intent").GetString());
        Assert.Equal("fr", router.RootElement.GetProperty("language").GetString());
        Assert.False(router.RootElement.GetProperty("needClarification").GetBoolean());
        var nativeCall = Assert.Single(nativeRouter.ToolCalls);
        Assert.Equal("route_direct_answer", nativeCall.Name);
        Assert.Equal("fr", nativeCall.Arguments.GetProperty("language").GetString());
        Assert.False(string.IsNullOrWhiteSpace(streamed.ToString()));
        Assert.Equal(3, metrics.Count);
        Assert.All(metrics, metric =>
        {
            Assert.True(metric.Success);
            Assert.Equal("openai", metric.Provider);
            Assert.Equal("gpt-5.6-terra", metric.ModelId);
            Assert.NotNull(metric.Usage.InputTokens);
            Assert.NotNull(metric.Usage.OutputTokens);
            Assert.NotNull(metric.EstimatedCostUsd);
        });

        var artifactDirectory = Environment.GetEnvironmentVariable("SAAIA_OPENAI_TERRA_ARTIFACT_DIR")
                                ?? Path.Combine(
                                    FindRepositoryRoot(),
                                    "artifacts",
                                    "openai-terra-provider-smoke-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(artifactDirectory);
        var artifactPath = Path.Combine(artifactDirectory, "result.json");
        await File.WriteAllTextAsync(
            artifactPath,
            JsonSerializer.Serialize(new
            {
                provider = provider.Descriptor,
                router = JsonDocument.Parse(routerJson).RootElement.Clone(),
                nativeRouter,
                writer = streamed.ToString(),
                metrics
            }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine("Artifact: " + artifactPath);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RAG.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("RAG.sln was not found.");
    }
}
