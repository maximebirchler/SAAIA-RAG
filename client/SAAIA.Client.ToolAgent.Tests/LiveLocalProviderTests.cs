using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveLocalProviderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Frozen_local_runtime_supports_common_provider_contract_when_explicitly_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_RUN_LOCAL_PROVIDER_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_RUN_LOCAL_PROVIDER_TEST=1 with the qualified local runtime already listening.");
            return;
        }

        var host = Environment.GetEnvironmentVariable("SAAIA_LOCAL_PROVIDER_HOST") ?? "127.0.0.1";
        var port = ReadPort(Environment.GetEnvironmentVariable("SAAIA_LOCAL_PROVIDER_PORT"), 1234);
        var model = Environment.GetEnvironmentVariable("SAAIA_LOCAL_PROVIDER_MODEL") ?? "local";
        var settings = new AppSettings
        {
            Host = host,
            Port = port,
            ModelId = model,
            ManageLocalLlmProcess = false
        };
        var provider = LlmProviderFactory.CreateLocal(new OpenAiLlmClient(), settings);
        provider.ConfigureGeneration(0, 384);
        var metrics = new List<LlmCallMetrics>();
        provider.CallCompleted += metrics.Add;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var models = await provider.ListModelsAsync(cancellation.Token);
        Assert.NotEmpty(models);

        var contract = LlmStructuredOutputContract.Parse(
            "saaia_local_smoke_router",
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
        using (provider.BeginTurn("local-provider-live-smoke"))
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
                maxTokens: 384,
                cancellation.Token,
                requireToolCall: true);
            await provider.StreamAsync(
                new[]
                {
                    (role: "system", content: "Tu es le Writer SAAIA. Réponds en une phrase courte."),
                    (role: "user", content: "Dis que le test du fournisseur local fonctionne, en français.")
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
            Assert.Equal("local", metric.Provider);
            Assert.Null(metric.EstimatedCostUsd);
        });

        var artifactDirectory = Environment.GetEnvironmentVariable("SAAIA_LOCAL_PROVIDER_ARTIFACT_DIR")
                                ?? Path.Combine(
                                    FindRepositoryRoot(),
                                    "artifacts",
                                    "local-provider-smoke-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
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

    private static int ReadPort(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : fallback;

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
