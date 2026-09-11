using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveRunPodProviderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RunPod_supports_model_listing_strict_router_json_and_streamed_writer_when_explicitly_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_RUN_RUNPOD_PROVIDER_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_RUN_RUNPOD_PROVIDER_TEST=1 for an explicitly paid RunPod probe.");
            return;
        }

        var configuration = LlmProviderConfiguration.Load();
        Assert.Equal(LlmProviderMode.RunPodBench, configuration.Mode);
        var provider = LlmProviderFactory.Create(
            new OpenAiLlmClient(),
            new AppSettings(),
            configuration);
        provider.ConfigureGeneration(0.2, 512);
        var metrics = new List<LlmCallMetrics>();
        provider.CallCompleted += metrics.Add;
        var models = await provider.ListModelsAsync(CancellationToken.None);
        Assert.Contains(provider.Descriptor.ModelId, models, StringComparer.OrdinalIgnoreCase);

        var contract = LlmStructuredOutputContract.Parse(
            "saaia_runpod_smoke_router",
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
        using (provider.BeginTurn("runpod-provider-live-smoke"))
        using (var cancellation = new CancellationTokenSource(
                   TimeSpan.FromSeconds(configuration.RunPod.RequestTimeoutSeconds)))
        {
            routerJson = await provider.CompleteStructuredAsync(
                new[]
                {
                    (role: "system", content: "Tu es un routeur SAAIA. Classe la demande selon le schéma."),
                    (role: "user", content: "Réponds directement en français.")
                },
                contract,
                cancellation.Token);
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
                cancellation.Token,
                requireToolCall: true);
            await provider.StreamAsync(
                new[]
                {
                    (role: "system", content: "Tu es le Writer SAAIA. Réponds en une phrase courte."),
                    (role: "user", content: "Dis que le test de streaming RunPod fonctionne, en français.")
                },
                forceJson: false,
                chunk => streamed.Append(chunk),
                cancellation.Token);
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
            Assert.Equal("runpod", metric.Provider);
            Assert.Equal(provider.Descriptor.ModelId, metric.ModelId);
        });

        var artifactDirectory = Environment.GetEnvironmentVariable("SAAIA_RUNPOD_ARTIFACT_DIR")
                                ?? Path.Combine(
                                    FindRepositoryRoot(),
                                    "artifacts",
                                    "runpod-provider-smoke-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(artifactDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, "result.json"),
            JsonSerializer.Serialize(new
            {
                provider = provider.Descriptor,
                availableModels = models,
                router = JsonDocument.Parse(routerJson).RootElement.Clone(),
                nativeRouter,
                writer = streamed.ToString(),
                metrics
            }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine("Artifact: " + artifactDirectory);
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
