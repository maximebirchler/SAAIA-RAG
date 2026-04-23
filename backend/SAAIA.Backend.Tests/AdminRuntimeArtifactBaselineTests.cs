using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SAAIA.Backend;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdminRuntimeArtifactBaselineTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    [Fact]
    public async Task AdminRuntime_deterministic_artifacts_match_versioned_cdc_baseline()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "false");

        try
        {
            var expected = JsonNode.Parse(await File.ReadAllTextAsync(ResolveFixturePath("runtime_admin_artifacts_baseline.v1.json")))!;
            var actual = await BuildStableArtifactBaselineAsync();

            Assert.Equal(expected.ToJsonString(JsonOptions), actual.ToJsonString(JsonOptions));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    private static async Task<JsonObject> BuildStableArtifactBaselineAsync()
    {
        var runtimeCatalog = await ExecuteResultAsync<AdminRuntimeRuntimeCatalogArtifactDto>(
            await AdminRuntimeEndpoints.RuntimeCatalogArtifactAsync(
                BuildAdminContext(),
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                Options.Create(CreateChatOptions()),
                new StubHostEnvironment()),
            BuildAdminContext());

        var modelCatalog = await ExecuteResultAsync<AdminRuntimeModelCatalogArtifactDto>(
            await AdminRuntimeEndpoints.ModelCatalogArtifactAsync(
                BuildAdminContext(),
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                Options.Create(CreateChatOptions()),
                new StubHostEnvironment()),
            BuildAdminContext());

        var warmupProfiles = await ExecuteResultAsync<AdminRuntimeWarmupProfilesArtifactDto>(
            await AdminRuntimeEndpoints.WarmupProfilesArtifactAsync(
                BuildAdminContext(),
                Options.Create(new RuntimeGovernanceOptions()),
                new StubHostEnvironment()),
            BuildAdminContext());

        var retrievalKpis = await ExecuteResultAsync<AdminRuntimeRetrievalKpisArtifactDto>(
            await AdminRuntimeEndpoints.RetrievalKpisArtifactAsync(
                BuildAdminContext(),
                new StubHostEnvironment(),
                Options.Create(new RuntimeGovernanceOptions())),
            BuildAdminContext());

        var capabilityAKpis = await ExecuteResultAsync<AdminRuntimeCapabilityAKpisArtifactDto>(
            await AdminRuntimeEndpoints.CapabilityAKpisArtifactAsync(
                BuildAdminContext(),
                new StubHostEnvironment(),
                Options.Create(new RuntimeGovernanceOptions())),
            BuildAdminContext());

        var capabilityBKpis = await ExecuteResultAsync<AdminRuntimeCapabilityBKpisArtifactDto>(
            await AdminRuntimeEndpoints.CapabilityBKpisArtifactAsync(
                BuildAdminContext(),
                new StubHostEnvironment(),
                Options.Create(new RuntimeGovernanceOptions())),
            BuildAdminContext());

        var serverCapabilityB = runtimeCatalog.Runtimes.Single(item => item.Key == "server-capability-b");

        return new JsonObject
        {
            ["version"] = "v1",
            ["artifacts"] = new JsonObject
            {
                [runtimeCatalog.Artifact] = new JsonObject
                {
                    ["artifact"] = runtimeCatalog.Artifact,
                    ["cdcAlignment"] = runtimeCatalog.CdcAlignment,
                    ["runtimeKeys"] = ToSortedJsonArray(runtimeCatalog.Runtimes.Select(item => item.Key)),
                    ["warmupProfileKeys"] = ToSortedJsonArray(runtimeCatalog.WarmupProfiles.Select(item => item.Key)),
                    ["capabilityKeys"] = ToSortedJsonArray(runtimeCatalog.Capabilities.Select(item => item.Key)),
                    ["serverCapabilityBDependencies"] = ToSortedJsonArray(serverCapabilityB.DependencyRuntimeKeys ?? [])
                },
                [modelCatalog.Artifact] = new JsonObject
                {
                    ["artifact"] = modelCatalog.Artifact,
                    ["cdcAlignment"] = modelCatalog.CdcAlignment,
                    ["itemKeys"] = ToSortedJsonArray(modelCatalog.Items.Select(item => item.Key))
                },
                [warmupProfiles.Artifact] = new JsonObject
                {
                    ["artifact"] = warmupProfiles.Artifact,
                    ["cdcAlignment"] = warmupProfiles.CdcAlignment,
                    ["profileKeys"] = ToSortedJsonArray(warmupProfiles.Items.Select(item => item.Key))
                },
                [retrievalKpis.Artifact] = BuildKpiSignature(retrievalKpis.Artifact, retrievalKpis.CdcAlignment, retrievalKpis.Metrics, retrievalKpis.Alerts),
                [capabilityAKpis.Artifact] = BuildKpiSignature(capabilityAKpis.Artifact, capabilityAKpis.CdcAlignment, capabilityAKpis.Metrics, capabilityAKpis.Alerts),
                [capabilityBKpis.Artifact] = BuildKpiSignature(capabilityBKpis.Artifact, capabilityBKpis.CdcAlignment, capabilityBKpis.Metrics, capabilityBKpis.Alerts)
            }
        };
    }

    private static JsonObject BuildKpiSignature(
        string artifact,
        string cdcAlignment,
        IReadOnlyList<AdminRuntimeMetricDefinitionDto> metrics,
        IReadOnlyList<AdminRuntimeAlertDefinitionDto> alerts)
        => new()
        {
            ["artifact"] = artifact,
            ["cdcAlignment"] = cdcAlignment,
            ["metricKeys"] = ToSortedJsonArray(metrics.Select(item => item.Key)),
            ["alertKeys"] = ToSortedJsonArray(alerts.Select(item => item.Key))
        };

    private static JsonArray ToSortedJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.OrderBy(static item => item, StringComparer.Ordinal))
            array.Add(value);
        return array;
    }

    private static string ResolveFixturePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static RagOptions CreateRagOptions()
        => new()
        {
            QdrantBaseUrl = "http://qdrant.test/",
            QdrantCollection = "knowledge_base",
            EmbeddingsBaseUrl = "http://tei.test/",
            EmbeddingsModel = "intfloat/multilingual-e5-base"
        };

    private static ChatOptions CreateChatOptions()
        => new()
        {
            LlmBaseUrl = "http://llm.test/",
            LlmModel = "local"
        };

    private static DefaultHttpContext BuildAdminContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonOptions>(_ => { });

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = services.BuildServiceProvider();
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = true;
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        return ctx;
    }

    private static async Task<T> ExecuteResultAsync<T>(IResult result, DefaultHttpContext ctx)
    {
        ctx.Response.Body.SetLength(0);
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SAAIA.Backend.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
