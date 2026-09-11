using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveSourceBackedCandidateRecoveryMicroprobeTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_qwen3_uses_role_and_anchor_memory_to_avoid_invalid_navigation_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_CANDIDATE_RECOVERY_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_CANDIDATE_RECOVERY_PROBE=1 "
                + "to run the candidate-recovery microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The candidate-recovery microprobe requires the local LLM URL and model.");
        }

        var intake = new SourceBackedIntake(
            "J'ai besoin d'un planning de repas pour la semaine du lundi au vendredi "
            + "incluant petit-dejeuner, dejeuner, collation et souper.",
            "rag.plan_repas",
            Array.Empty<string>(),
            new[]
            {
                "row:Lundi", "row:Mardi", "row:Mercredi", "row:Jeudi", "row:Vendredi",
                "column:Petit-dejeuner", "column:Dejeuner", "column:Collation", "column:Souper"
            },
            AllowsPartialAnswer: false,
            Language: "fr",
            CatalogHints: new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine", "Cuisine", 10, Array.Empty<string>())
            })
        {
            CanonicalColumnSemanticRoles =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Petit-dejeuner"] =
                        "IN_SCOPE: Repas matinal comprenant du lait, du pain, des oeufs ou du cafe. | OUT_OF_SCOPE: Repas principal, collation ou souper du jour.",
                    ["Dejeuner"] =
                        "IN_SCOPE: Repas principal de l'apres-midi avec viande, legumes ou cereales. | OUT_OF_SCOPE: Petit-dejeuner, collation ou souper du jour.",
                    ["Collation"] =
                        "IN_SCOPE: Gouter ou aliment leger entre les repas principaux, comme un fruit ou une barre. | OUT_OF_SCOPE: Repas complet ou souper du jour.",
                    ["Souper"] =
                        "IN_SCOPE: Repas de fin de journee, simple et equilibre avec proteines et legumes. | OUT_OF_SCOPE: Petit-dejeuner, dejeuner ou collation du jour."
                }
        };
        var executedRequests = BuildExecutedRequests();
        const string semanticPlan =
            "LIVRABLE: planning source\nDIMENSIONS: 5 x 4\n"
            + "PREUVES_ATOMIQUES: 20 instances nommees et sourcees\n"
            + "ACCEPTER_SI: vingt instances distinctes sont disponibles";
        var navigationBundle = BuildNavigationBundle();
        var resolvedNavigationEvidenceIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            SourceBackedAgentV2Options.ResolveFromEnvironment());
        var auditedNavigationEvidenceIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var anchorAudit = await InvokePrivateInstanceAsync(
            runner,
            "CompleteNavigationAnchorEligibilityAuditAsync",
            intake,
            semanticPlan,
            "Produit alimentaire",
            "Le libelle doit nommer une instance alimentaire specifique et autonome.",
            navigationBundle,
            resolvedNavigationEvidenceIds,
            auditedNavigationEvidenceIds,
            cts.Token);
        navigationBundle = ReadProperty<EvidenceBundle>(anchorAudit, "Bundle");
        output.WriteLine(
            "Anchor audit: candidates="
            + ReadProperty<int>(anchorAudit, "CandidateCount")
            + " eligible="
            + ReadProperty<int>(anchorAudit, "EligibleCount")
            + " calls="
            + ReadProperty<int>(anchorAudit, "LlmCallCount"));
        var messages = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentMessage>>(InvokePrivateStatic(
            "BuildResearchTransitionDecisionMessages",
            intake,
            semanticPlan,
            "Produit alimentaire",
            "Le libelle doit nommer une instance alimentaire specifique et autonome.",
            navigationBundle,
            resolvedNavigationEvidenceIds,
            executedRequests,
            new[] { "Cuisine" },
            19,
            20,
            "COUVERTURE SEMANTIQUE DU VIVIER INSUFFISANTE: les compatibilites decidees par le LLM permettent un appariement distinct de 19 cellule(s) sur 20. Capacites documentaires par colonne: Petit-dejeuner=4/5 | Dejeuner=12/5 | Collation=7/5 | Souper=10/5. Poursuis la collecte en ciblant librement les lacunes semantiques; ne lance pas encore la redaction.",
            40));
        var tools = BuildResearchTransitionTools(
            executedRequests,
            navigationBundle);

        var completions = new List<SourceBackedAgentCompletion>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var completion = await innerLlm.ChatOnceNativeAsync(
                messages,
                tools,
                temperature: 0,
                maxTokens: 256,
                cts.Token,
                requireToolCall: true);
            var call = Assert.Single(completion.ToolCalls);
            output.WriteLine(
                "Attempt " + attempt + ": "
                + call.Name + " " + call.Arguments.GetRawText());
            Assert.NotEqual("resolve_navigation_anchors", call.Name);
            if (call.Name is "start_content_card_research"
                or "start_document_search"
                or "refine_document_navigation")
            {
                var query = GetString(call.Arguments, "query");
                Assert.False(string.IsNullOrWhiteSpace(query));
                Assert.DoesNotContain(
                    ExistingCandidateNames,
                    name => query.Contains(
                        name,
                        StringComparison.OrdinalIgnoreCase));
            }
            completions.Add(completion);
        }

        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(
            artifact,
            BuildReport(
                model,
                llmBaseUrl,
                string.Join(
                    "\n---\n",
                    messages.Select(static message => message.Content)),
                tools,
                completions),
            CancellationToken.None);
        output.WriteLine("Artifact: " + artifact);
    }

    private static IReadOnlyList<RetrievalRequest> BuildExecutedRequests()
        => new[]
        {
            Request(string.Empty, 0, 20, 20, 20),
            Request(string.Empty, 20, 40, 20, 20)
        };

    private static RetrievalRequest Request(
        string query,
        int offset,
        int? nextOffset,
        int materializedEvidenceCount,
        int newEvidenceCount)
    {
        var arguments = query.Length == 0
            ? JsonSerializer.SerializeToElement(new
            {
                categoryPath = "Cuisine",
                inventoryMode = "representative",
                limit = 20,
                offset
            })
            : JsonSerializer.SerializeToElement(new
            {
                categoryPath = "Cuisine",
                q = query,
                limit = 20,
                offset
            });
        return new RetrievalRequest(
            "documents.content_cards",
            query,
            "Cuisine",
            "live_candidate_recovery_probe",
            Limit: 20,
            Offset: offset,
            NextOffset: nextOffset,
            MaterializedEvidenceCount: materializedEvidenceCount,
            NewEvidenceCount: newEvidenceCount,
            ToolArguments: arguments,
            NewEvidenceIds: Enumerable.Range(offset + 1, newEvidenceCount)
                .Select(static index => "R" + index)
                .ToArray(),
            SemanticAuditApprovedCount: offset == 0 ? 12 : 7,
            SemanticAuditRejectedCount: offset == 0 ? 8 : 13,
            SemanticCompatibilityCounts:
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Petit-dejeuner"] = 2,
                    ["Dejeuner"] = offset == 0 ? 7 : 5,
                    ["Collation"] = offset == 0 ? 4 : 3,
                    ["Souper"] = offset == 0 ? 6 : 4
                });
    }

    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildResearchTransitionTools(
            IReadOnlyList<RetrievalRequest> executedRequests,
            EvidenceBundle navigationBundle)
    {
        return Assert.IsAssignableFrom<IReadOnlyList<SourceBackedAgentToolDefinition>>(
            InvokePrivateStatic(
                "BuildResearchTransitionTools",
                navigationBundle,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                executedRequests,
                40));
    }

    private static EvidenceBundle BuildNavigationBundle()
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.navigation",
            DurationMs = 0,
            Result = JsonSerializer.SerializeToElement(new
            {
                navigationOnly = true,
                total = 2,
                limit = 2,
                offset = 0,
                items = new[]
                {
                    new
                    {
                        label = "Planification generale",
                        kind = "navigation_entry",
                        docId = "doc-a",
                        docName = "guide-a.pdf",
                        docPath = "Cuisine/guide-a.pdf",
                        targetChunkId = "chunk-a",
                        targetPageStart = 1,
                        targetPageEnd = 1
                    },
                    new
                    {
                        label = "Introduction",
                        kind = "navigation_entry",
                        docId = "doc-b",
                        docName = "guide-b.pdf",
                        docPath = "Cuisine/guide-b.pdf",
                        targetChunkId = "chunk-b",
                        targetPageStart = 1,
                        targetPageEnd = 1
                    }
                }
            })
        });
        var navigationBundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "candidate recovery probe");
        var candidates = new[]
        {
            Candidate("E3", "Crepes", 3, "Petit-dejeuner", "Collation"),
            Candidate("E4", "Muffins", 4, "Petit-dejeuner", "Collation"),
            Candidate("E5", "Creme au fromage", 5, "Petit-dejeuner"),
            Candidate("E6", "Caramel", 6, "Petit-dejeuner", "Collation"),
            Candidate("E7", "Tartiflette", 7, "Dejeuner", "Souper"),
            Candidate("E8", "Gratin dauphinois", 8, "Dejeuner", "Souper"),
            Candidate("E9", "Osso buco", 9, "Dejeuner"),
            Candidate("E10", "Aligot", 10, "Dejeuner", "Souper"),
            Candidate("E11", "Garbure", 11, "Dejeuner"),
            Candidate("E12", "Couscous", 12, "Dejeuner", "Souper"),
            Candidate("E13", "Blanquette", 13, "Dejeuner"),
            Candidate("E14", "Quiche", 14, "Dejeuner", "Souper"),
            Candidate("E15", "Gigot", 15, "Dejeuner", "Souper"),
            Candidate("E16", "Magret", 16, "Dejeuner", "Souper"),
            Candidate("E17", "Moules", 17, "Souper"),
            Candidate("E18", "Pomme au four", 18, "Collation"),
            Candidate("E19", "Compote", 19, "Collation"),
            Candidate("E20", "Gateau au yaourt", 20, "Collation"),
            Candidate("E21", "Fruit de saison", 21, "Collation")
        };
        return navigationBundle with
        {
            Items = navigationBundle.Items.Concat(candidates).ToArray()
        };
    }

    private static readonly string[] ExistingCandidateNames =
    {
        "Crepes", "Muffins", "Creme au fromage", "Caramel", "Tartiflette",
        "Gratin dauphinois", "Osso buco", "Aligot", "Garbure", "Couscous",
        "Blanquette", "Quiche", "Gigot", "Magret", "Moules", "Pomme au four",
        "Compote", "Gateau au yaourt", "Fruit de saison"
    };

    private static EvidenceItem Candidate(
        string evidenceId,
        string displayValue,
        int page,
        params string[] compatibleColumns)
        => new(
            evidenceId,
            "canonical_content_card",
            "documents.content_cards",
            string.Empty,
            "doc-candidates",
            "candidates.pdf",
            "Cuisine/candidates.pdf",
            null,
            null,
            page,
            page,
            "content-card:" + evidenceId.ToLowerInvariant(),
            displayValue,
            displayValue.ToLowerInvariant(),
            1,
            page,
            "Cuisine",
            "fr",
            "fr",
            "text",
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["semanticDisplayValue"] = displayValue,
                ["semanticCompatibleColumnLabels"] =
                    JsonSerializer.Serialize(compatibleColumns)
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>(),
            Array.Empty<string>());

    private static string? GetString(JsonElement arguments, params string[] names)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in arguments.EnumerateObject())
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString()?.Trim() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static int? GetInt(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in arguments.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }
        return null;
    }

    private static object InvokePrivateStatic(
        string methodName,
        params object?[] arguments)
    {
        var method = typeof(SourceBackedAgentV2Runner)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(candidate => candidate.Name == methodName)
            .Single(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == arguments.Length
                       && parameters.Zip(arguments).All(static pair =>
                           pair.Second is null
                           || pair.First.ParameterType.IsInstanceOfType(pair.Second));
            });
        var result = method.Invoke(null, arguments);
        Assert.NotNull(result);
        return result!;
    }

    private static async Task<object> InvokePrivateInstanceAsync(
        object instance,
        string methodName,
        params object?[] arguments)
    {
        var method = instance.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(candidate => candidate.Name == methodName)
            .Single(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == arguments.Length
                       && parameters.Zip(arguments).All(static pair =>
                           pair.Second is null
                           || pair.First.ParameterType.IsInstanceOfType(pair.Second));
            });
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(instance, arguments));
        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        Assert.NotNull(result);
        return result!;
    }

    private static T ReadProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(instance));
    }

    private sealed class UnusedToolExecutor : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "The candidate-recovery microprobe does not execute documentary tools.");
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
                Math.Clamp(maxTokens, 64, 1200),
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
                Math.Clamp(maxTokens, 64, 1200),
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

    private static string BuildReport(
        string model,
        string llmBaseUrl,
        string decisionPrompt,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        IReadOnlyList<SourceBackedAgentCompletion> completions)
    {
        var report = new StringBuilder();
        report.AppendLine("LIVE SOURCE-BACKED CANDIDATE RECOVERY MICROPROBE");
        report.AppendLine("MODEL: " + model);
        report.AppendLine("LLM_URL: " + NormalizeLlmBaseUrl(llmBaseUrl));
        report.AppendLine("ATTEMPTS: " + completions.Count);
        report.AppendLine("PROMPT_TOKENS_TOTAL: " + completions.Sum(static item => item.PromptTokens));
        report.AppendLine("COMPLETION_TOKENS_TOTAL: " + completions.Sum(static item => item.CompletionTokens));
        report.AppendLine("TOOLS_EXPOSED: " + string.Join(", ", tools.Select(static tool => tool.Name)));
        report.AppendLine();
        report.AppendLine("DECISION_PROMPT:");
        report.AppendLine(decisionPrompt);
        report.AppendLine();
        report.AppendLine("TOOL_CALLS:");
        foreach (var (completion, index) in completions.Select(
                     static (item, index) => (item, index)))
        {
            report.AppendLine("ATTEMPT " + (index + 1));
            report.AppendLine("RAW_CONTENT: " + completion.Content);
            foreach (var call in completion.ToolCalls)
                report.AppendLine(call.Name + " " + call.Arguments.GetRawText());
        }
        return report.ToString();
    }

    private static string NormalizeLlmBaseUrl(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        return normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + "/v1";
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string CreateArtifactPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
               && !Directory.Exists(Path.Combine(current.FullName, ".git")))
        {
            current = current.Parent;
        }
        if (current is null)
            throw new InvalidOperationException("Could not locate the repository root.");
        return Path.Combine(
            current.FullName,
            "artifacts",
            "live-source-backed-candidate-recovery-"
            + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            "report.txt");
    }
}
