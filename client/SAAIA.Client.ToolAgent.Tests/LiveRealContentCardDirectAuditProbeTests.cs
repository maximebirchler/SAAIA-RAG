using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveRealContentCardDirectAuditProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_qwen3_audits_real_content_card_titles_in_adaptive_batches_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_REAL_CONTENT_CARD_DIRECT_AUDIT_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_REAL_CONTENT_CARD_DIRECT_AUDIT_PROBE=1 to run the live probe.");
            return;
        }

        var settings = AppSettings.Load();
        var backendUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_BACKEND_URL"),
            settings.BackendUrl);
        var apiKey = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_API_KEY"),
            SecureLocalStore.GetServerApiKey());
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(backendUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The live direct-card audit probe requires backend, API key and local LLM settings.");
        }

        var candidateCount = ReadInt(
            "SAAIA_LIVE_REAL_CONTENT_CARD_DIRECT_AUDIT_CANDIDATES",
            fallback: 40,
            minimum: 1,
            maximum: 80);
        var contextTokens = ReadInt(
            "SAAIA_LIVE_REAL_CONTENT_CARD_DIRECT_AUDIT_CONTEXT_TOKENS",
            fallback: 4096,
            minimum: 2048,
            maximum: 32768);
        var candidateOffset = ReadInt(
            "SAAIA_LIVE_REAL_CONTENT_CARD_DIRECT_AUDIT_OFFSET",
            fallback: 0,
            minimum: 0,
            maximum: 10000);
        var auditBatchSize = ReadInt(
            "SAAIA_LIVE_REAL_CONTENT_CARD_DIRECT_AUDIT_BATCH_SIZE",
            fallback: candidateCount,
            minimum: 1,
            maximum: candidateCount);
        var configuredQuery = Environment.GetEnvironmentVariable(
            "SAAIA_LIVE_REAL_CONTENT_CARD_DIRECT_AUDIT_QUERY");
        var query = string.IsNullOrWhiteSpace(configuredQuery)
            ? null
            : configuredQuery.Trim();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));
        var raw = await api.DocumentsContentCardsAsync(
            "Cuisine",
            null,
            null,
            null,
            query,
            "representative",
            candidateCount,
            candidateOffset,
            cts.Token);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.content_cards",
            Result = raw.Clone(),
            DurationMs = 0
        });
        var originalBundle = EvidenceBundleBuilder.FromToolResults(
            toolResults,
            "Construire un planning de repas documente du lundi au vendredi.");
        var candidates = originalBundle.Items
            .Select(candidate =>
            {
                var title = ReadContentCardTitle(candidate);
                var hints = new Dictionary<string, string>(
                    candidate.SelectionHints,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["sourceAnchorLabel"] = title,
                    ["directCardTitleProbe"] = "true"
                };
                return candidate with { SelectionHints = hints };
            })
            .ToArray();

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: candidateCount,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900,
                MaximumContextTokens: contextTokens,
                MaximumSemanticCandidatesPerAuditTurn: candidateCount,
                MaximumSemanticCandidateAuditConcurrency: 1,
                SemanticCandidateLabelResolutionEnabled: true,
                MaximumSemanticCandidatesPerAuditBatch: auditBatchSize));
        var intake = BuildMealPlanIntake();
        var execution = await InvokePrivateAsync(
            runner,
            "CompleteBatchedCandidateAuditAsync",
            intake,
            BuildMealPlan(),
            string.Empty,
            string.Empty,
            "Jour",
            new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
            new[] { "Petit-dejeuner", "Dejeuner", "Collation", "Souper" },
            candidates,
            cts.Token);

        var completion = ReadProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        var decision = ReadProperty<object>(execution, "Decision");
        var approvedIds = ReadObjects(decision, "ApprovedCandidates")
            .Select(item => ReadProperty<string>(item, "EvidenceId"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var classifications = ReadObjects(decision, "Classifications")
            .ToDictionary(
                item => ReadProperty<string>(item, "EvidenceId"),
                item => ReadProperty<string>(item, "Classification"),
                StringComparer.OrdinalIgnoreCase);

        var report = new StringBuilder()
            .AppendLine("LIVE REAL CONTENT-CARD DIRECT AUDIT PROBE")
            .AppendLine("BACKEND: " + backendUrl)
            .AppendLine("MODEL: " + model)
            .AppendLine("QUERY: " + (query ?? "(none)"))
            .AppendLine("CONTEXT_TOKENS: " + contextTokens)
            .AppendLine("CANDIDATE_OFFSET: " + candidateOffset)
            .AppendLine("CANDIDATE_COUNT: " + candidates.Length)
            .AppendLine("AUDIT_BATCH_SIZE: " + auditBatchSize)
            .AppendLine("PROTOCOL_VALID: " + ReadProperty<bool>(decision, "ProtocolValid"))
            .AppendLine("APPROVED_COUNT: " + approvedIds.Count)
            .AppendLine("REJECTED_COUNT: " + (candidates.Length - approvedIds.Count))
            .AppendLine("LLM_CALLS: " + ReadProperty<int>(execution, "LlmCallCount"))
            .AppendLine("LABEL_REVIEW_LLM_CALLS: " + ReadProperty<int>(execution, "LabelReviewLlmCallCount"))
            .AppendLine("EXECUTED_BATCHES: " + ReadProperty<int>(execution, "ExecutedBatchCount"))
            .AppendLine("INPUT_BUDGET_SPLITS: " + ReadProperty<int>(execution, "InputBudgetSplitCount"))
            .AppendLine("MAXIMUM_MEASURED_INPUT_TOKENS: "
                + (execution.GetType().GetProperty("MaximumMeasuredInputTokens")!.GetValue(execution)?.ToString()
                   ?? "unavailable"))
            .AppendLine("ELAPSED_MS: " + ReadProperty<long>(execution, "ElapsedMilliseconds"))
            .AppendLine()
            .AppendLine("CANDIDATE DECISIONS");
        foreach (var candidate in candidates)
        {
            var decisionLabel = approvedIds.Contains(candidate.EvidenceId)
                ? "ACCEPT"
                : classifications.GetValueOrDefault(candidate.EvidenceId, "REJECT");
            report.Append(candidate.EvidenceId)
                .Append(" | ").Append(decisionLabel)
                .Append(" | ").Append(ReadContentCardTitle(candidate))
                .Append(" | ").Append(candidate.SelectionHints.GetValueOrDefault("headingPath", string.Empty))
                .Append(" | ").Append(candidate.DocPath)
                .Append(" | p.").Append(candidate.PageStart)
                .AppendLine();
        }
        report.AppendLine()
            .AppendLine("RAW")
            .AppendLine(completion.Content);

        var artifactDirectory = Path.Combine(
            FindRepoRoot(),
            "artifacts",
            "live-real-content-card-direct-audit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(artifactDirectory);
        var artifact = Path.Combine(artifactDirectory, "report.txt");
        await File.WriteAllTextAsync(artifact, report.ToString(), CancellationToken.None);
        output.WriteLine("Artifact: " + artifact);
        output.WriteLine("Approved: " + approvedIds.Count + "/" + candidates.Length);

        Assert.NotEmpty(candidates);
        Assert.True(ReadProperty<bool>(decision, "ProtocolValid"), completion.Content);
        Assert.True(ReadProperty<int>(execution, "LlmCallCount") >= 1);
        Assert.Equal(0, ReadProperty<int>(execution, "LabelReviewLlmCallCount"));
        Assert.NotEmpty(approvedIds);
    }

    private static SourceBackedIntake BuildMealPlanIntake()
        => new(
            "J'ai besoin d'un planning de repas pour la semaine du lundi au vendredi incluant petit-dejeuner, dejeuner, collation et souper.",
            "rag.plan_repas",
            new[] { "5 jours", "4 moments", "20 noms distincts" },
            Enumerable.Range(1, 20).Select(static index => "case:" + index).ToArray(),
            AllowsPartialAnswer: false,
            Language: "fr")
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine",
                    "Cuisine",
                    10,
                    Array.Empty<string>())
            }
        };

    private static string BuildMealPlan()
        => """
           LIVRABLE: planning de repas avec vingt preparations documentees
           DIMENSIONS: cinq jours x quatre moments
           PREUVES_ATOMIQUES: vingt preparations culinaires nommees distinctes
           ACCEPTER_SI: vingt titres distincts sont prouves par fichier et page
           INSUFFISANT_SEULEMENT_SI: moins de vingt noms distincts sont prouves
           """;

    private static string ReadContentCardTitle(EvidenceItem item)
    {
        if (item.MatchedContentCards is { } cards
            && cards.ValueKind == JsonValueKind.Array
            && cards.GetArrayLength() > 0
            && cards[0].TryGetProperty("title", out var title)
            && title.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(title.GetString()))
        {
            return title.GetString()!.Trim();
        }
        return (item.Excerpt ?? string.Empty).Trim();
    }

    private static async Task<object> InvokePrivateAsync(
        SourceBackedAgentV2Runner runner,
        string methodName,
        params object?[] arguments)
    {
        var method = typeof(SourceBackedAgentV2Runner)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(candidate => candidate.Name == methodName)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == arguments.Length
                       && parameters.Zip(arguments).All(static pair =>
                           pair.Second is null
                           || pair.First.ParameterType.IsInstanceOfType(pair.Second));
            });
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(runner, arguments));
        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        Assert.NotNull(result);
        return result!;
    }

    private static T ReadProperty<T>(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property!.GetValue(value));
    }

    private static IReadOnlyList<object> ReadObjects(object value, string propertyName)
        => ((IEnumerable)value.GetType().GetProperty(propertyName)!.GetValue(value)!)
            .Cast<object>()
            .ToArray();

    private static string NormalizeLlmBaseUrl(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        return normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + "/v1";
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static int ReadInt(
        string variable,
        int fallback,
        int minimum,
        int maximum)
        => int.TryParse(Environment.GetEnvironmentVariable(variable), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RAG.sln")))
            directory = directory.Parent;
        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class NativeLlmAdapter(OpenAiLlmClient inner)
        : ISourceBackedAgentLlmClient,
          ISourceBackedAgentStructuredLlmClient,
          ISourceBackedAgentInputTokenCounter
    {
        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
            => inner.ChatOnceNativeAsync(
                messages,
                tools,
                temperatureOverride ?? 0.1,
                Math.Clamp(maxTokens, 128, 1200),
                ct,
                requireToolCall);

        public async Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null)
        {
            var content = await inner.ChatOnceStructuredAsync(
                messages.Select(static message =>
                    (message.Role, message.Content ?? string.Empty)).ToArray(),
                temperatureOverride ?? 0,
                Math.Clamp(maxTokens, 128, 1200),
                contract,
                ct);
            return new SourceBackedAgentCompletion(
                content,
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop");
        }

        public Task<int?> CountInputTokensAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            CancellationToken ct,
            bool requireToolCall = false)
            => inner.CountNativeInputTokensAsync(
                messages,
                tools,
                ct,
                requireToolCall);
    }

    private sealed class UnusedToolExecutor : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "The direct-card audit probe does not execute retrieval tools.");
    }
}
