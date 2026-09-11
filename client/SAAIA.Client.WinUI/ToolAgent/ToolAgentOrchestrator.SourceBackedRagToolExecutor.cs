using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed class OrchestratorSourceBackedRagToolExecutor :
        ISourceBackedRagToolExecutor,
        ISourceBackedAgentToolExecutor
    {
        private readonly ToolAgentOrchestrator _owner;
        private readonly Action<string>? _onPhase;
        private readonly Action<string>? _onProgress;
        private readonly int? _maximumNativeContentCardItems;

        public OrchestratorSourceBackedRagToolExecutor(
            ToolAgentOrchestrator owner,
            Action<string>? onPhase,
            Action<string>? onProgress,
            int? maximumNativeContentCardItems = null)
        {
            _owner = owner;
            _onPhase = onPhase;
            _onProgress = onProgress;
            _maximumNativeContentCardItems = maximumNativeContentCardItems;
        }

        public Task<ToolResults> ExecuteAsync(SourceBackedIntake intake, RetrievalPlan plan, CancellationToken ct)
        {
            var scoped = SourceBackedRetrievalScope.ApplyDefaultCategoryScope(intake, plan);
            if (scoped.AppliedRequestCount > 0)
            {
                _owner.EmitRagTrace(
                    "source_backed_scope.default_category.applied",
                    ("categoryPath", scoped.DefaultCategoryPath),
                    ("requests", scoped.AppliedRequestCount),
                    ("tools", scoped.Plan.Requests.Select(static request => request.ToolName).ToArray()));
            }

            var routerPlan = SourceBackedRouterPlanAdapter.ToRouterPlan(intake, scoped.Plan);
            return _owner.ExecuteToolsAsync(
                routerPlan,
                intake.UserQuestion,
                ct,
                _onPhase,
                _onProgress,
                skipInitialSourceBackedCategoryPlanning: true);
        }

        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            if (string.Equals(
                    toolName,
                    "documents.context_batch",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteNativeSourceBackedContextBatchAsync(
                    intake,
                    arguments,
                    ct);
            }
            if (toolName is not ("rag.search" or "rag.multi_search"
                or "documents.navigation" or "documents.content_cards" or "documents.context"))
            {
                throw new InvalidOperationException($"Tool '{toolName}' is not allowed in the source-backed agent loop.");
            }

            return ExecuteNativeSourceBackedToolAsync(
                intake,
                toolName,
                PrepareNativeSourceBackedArguments(intake, toolName, arguments),
                ct);
        }

        private async Task<ToolResults> ExecuteNativeSourceBackedToolAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            var results = new ToolResults();
            _onPhase?.Invoke(PhaseLabelForTool(toolName, intake.Language));
            _onProgress?.Invoke(DescribeToolAction(
                toolName,
                intake.UserQuestion,
                intake.Language,
                arguments));

            var stopwatch = Stopwatch.StartNew();
            _owner.EmitRagTrace(
                "source_backed_native_tool.start",
                ("name", toolName),
                ("args", arguments.GetRawText()));
            ClientLog.Info(
                "ToolAgent source-backed native tool start: " +
                $"name={toolName}|args={TruncateForPrompt(arguments.GetRawText(), 420)}");
            try
            {
                var result = toolName switch
                {
                    "rag.search" => await _owner.ExecRagSearchAsync(arguments, ct)
                        .ConfigureAwait(false),
                    "rag.multi_search" => await _owner.ExecRagMultiSearchAsync(arguments, ct)
                        .ConfigureAwait(false),
                    "documents.navigation" => await _owner.ExecDocumentsNavigationAsync(arguments, ct)
                        .ConfigureAwait(false),
                    "documents.content_cards" => await _owner.ExecDocumentsContentCardsAsync(arguments, ct)
                        .ConfigureAwait(false),
                    "documents.context" => await _owner.ExecDocumentsContextAsync(arguments, ct)
                        .ConfigureAwait(false),
                    _ => throw new InvalidOperationException(
                        $"Tool '{toolName}' is not allowed in the source-backed agent loop.")
                };

                results.Items.Add(new ToolResults.Item
                {
                    ToolName = toolName,
                    Result = result,
                    DurationMs = stopwatch.ElapsedMilliseconds
                });
                _owner.EmitRagTrace(
                    "source_backed_native_tool.end",
                    ("name", toolName),
                    ("ok", true),
                    ("result_kind", result.ValueKind.ToString()),
                    ("result_chars", result.GetRawText().Length),
                    ("ms", stopwatch.ElapsedMilliseconds));
                ClientLog.Info(
                    "ToolAgent source-backed native tool end: " +
                    $"name={toolName}|ok=true|ms={stopwatch.ElapsedMilliseconds}|" +
                    $"resultKind={result.ValueKind}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var structuredError = ClassifyToolExecutionError(
                    ex,
                    isAdminTool: false);
                var effectiveError = string.IsNullOrWhiteSpace(structuredError)
                    ? ex.Message
                    : structuredError;
                var resultError = string.IsNullOrWhiteSpace(structuredError)
                    ? "tool_failed"
                    : structuredError;
                results.Items.Add(new ToolResults.Item
                {
                    ToolName = toolName,
                    Error = effectiveError,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Result = JsonSerializer.SerializeToElement(new
                    {
                        error = resultError
                    }, ClientJson.CamelCase)
                });
                _owner.EmitRagTrace(
                    "source_backed_native_tool.end",
                    ("name", toolName),
                    ("ok", false),
                    ("error", effectiveError),
                    ("ms", stopwatch.ElapsedMilliseconds));
                ClientLog.Info(
                    "ToolAgent source-backed native tool end: " +
                    $"name={toolName}|ok=false|" +
                    $"error={TruncateForPrompt(effectiveError, 260)}|" +
                    $"ms={stopwatch.ElapsedMilliseconds}");
            }

            return results;
        }

        private async Task<ToolResults>
            ExecuteNativeSourceBackedContextBatchAsync(
                SourceBackedIntake intake,
                JsonElement arguments,
                CancellationToken ct)
        {
            if (arguments.ValueKind != JsonValueKind.Object
                || !arguments.TryGetProperty("targets", out var targetsElement)
                || targetsElement.ValueKind != JsonValueKind.Array
                || targetsElement.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    "documents.context_batch requires resolved targets.");
            }

            var targets = targetsElement.EnumerateArray()
                .Where(static target => target.ValueKind == JsonValueKind.Object)
                .Select(static target => target.Clone())
                .ToArray();
            if (targets.Length == 0
                || targets.Length != targetsElement.GetArrayLength())
            {
                throw new InvalidOperationException(
                    "documents.context_batch contains an invalid target.");
            }

            _onPhase?.Invoke(PhaseLabelForTool("documents.context", intake.Language));
            _onProgress?.Invoke(DescribeToolAction(
                "documents.context",
                intake.UserQuestion,
                intake.Language,
                arguments));
            _owner.EmitRagTrace(
                "source_backed_native_context_batch.start",
                ("targets", targets.Length));
            ClientLog.Info(
                "ToolAgent source-backed native context batch start: "
                + $"targets={targets.Length}");

            var stopwatch = Stopwatch.StartNew();
            var tasks = targets
                .Select(target => ExecuteNativeContextBatchTargetAsync(target, ct))
                .ToArray();
            var items = await Task.WhenAll(tasks).ConfigureAwait(false);
            var results = new ToolResults();
            foreach (var item in items)
                results.Items.Add(item);

            var failed = items.Count(static item =>
                !string.IsNullOrWhiteSpace(item.Error));
            _owner.EmitRagTrace(
                "source_backed_native_context_batch.end",
                ("targets", targets.Length),
                ("succeeded", targets.Length - failed),
                ("failed", failed),
                ("ms", stopwatch.ElapsedMilliseconds));
            ClientLog.Info(
                "ToolAgent source-backed native context batch end: "
                + $"targets={targets.Length}|failed={failed}|"
                + $"ms={stopwatch.ElapsedMilliseconds}");
            return results;
        }

        private async Task<ToolResults.Item> ExecuteNativeContextBatchTargetAsync(
            JsonElement arguments,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await _owner.ExecDocumentsContextAsync(arguments, ct)
                    .ConfigureAwait(false);
                return new ToolResults.Item
                {
                    ToolName = "documents.context",
                    Result = AttachNavigationAnchorLineage(
                        result,
                        arguments),
                    DurationMs = stopwatch.ElapsedMilliseconds
                };
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var structuredError = ClassifyToolExecutionError(
                    ex,
                    isAdminTool: false);
                var effectiveError = string.IsNullOrWhiteSpace(structuredError)
                    ? ex.Message
                    : structuredError;
                return new ToolResults.Item
                {
                    ToolName = "documents.context",
                    Error = effectiveError,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Result = JsonSerializer.SerializeToElement(new
                    {
                        error = string.IsNullOrWhiteSpace(structuredError)
                            ? "tool_failed"
                            : structuredError
                    }, ClientJson.CamelCase)
                };
            }
        }

        private static JsonElement AttachNavigationAnchorLineage(
            JsonElement result,
            JsonElement target)
        {
            if (result.ValueKind != JsonValueKind.Object
                || target.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            var properties = result.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
            CopyBatchLineageProperty(
                target,
                "evidenceId",
                properties,
                "sourceAnchorEvidenceId");
            CopyBatchLineageProperty(
                target,
                "sourceAnchorLabel",
                properties,
                "sourceAnchorLabel");
            CopyBatchLineageProperty(
                target,
                "anchorId",
                properties,
                "sourceAnchorId");
            CopyBatchLineageProperty(
                target,
                "chunkId",
                properties,
                "sourceAnchorChunkId");
            return JsonSerializer.SerializeToElement(
                properties,
                ClientJson.CamelCase);
        }

        private static void CopyBatchLineageProperty(
            JsonElement source,
            string sourceName,
            IDictionary<string, JsonElement> destination,
            string destinationName)
        {
            if (!source.TryGetProperty(sourceName, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                return;
            }

            destination[destinationName] = value.Clone();
        }

        private JsonElement PrepareNativeSourceBackedArguments(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments)
        {
            var normalizedArguments =
                SourceBackedAgentToolArgumentNormalizer.NormalizeDocumentLocator(arguments);
            var properties = normalizedArguments.ValueKind == JsonValueKind.Object
                ? normalizedArguments.EnumerateObject().ToDictionary(
                    static property => property.Name,
                    static property => property.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (toolName is "rag.search" or "rag.multi_search")
            {
                properties["sourceBackedCanonical"] = JsonSerializer.SerializeToElement(true);
                properties["disableAutomaticCategoryScoping"] = JsonSerializer.SerializeToElement(true);
                properties["includeResearchSurfaces"] = JsonSerializer.SerializeToElement(true);
                if (!properties.ContainsKey("researchMode"))
                {
                    properties["researchMode"] = JsonSerializer.SerializeToElement("source_exploration");
                }
            }

            if (toolName == "documents.content_cards"
                && _maximumNativeContentCardItems is > 0)
            {
                var requestedLimit = ReadPositiveInt(properties, "limit");
                properties["limit"] = JsonSerializer.SerializeToElement(
                    Math.Min(
                        requestedLimit ?? _maximumNativeContentCardItems.Value,
                        _maximumNativeContentCardItems.Value));
            }

            if (toolName is
                    "documents.navigation"
                    or "documents.context"
                    or "documents.content_cards"
                && !HasNonBlankString(properties, "docRef")
                && !HasNonBlankString(properties, "docPath")
                && !HasNonBlankString(properties, "docId"))
            {
                var requestedDocument = SourceBackedQuestionFocus.ExtractFirstSpecificDocumentName(intake);
                if (!string.IsNullOrWhiteSpace(requestedDocument))
                    properties["docRef"] = JsonSerializer.SerializeToElement(requestedDocument);
            }

            return JsonSerializer.SerializeToElement(properties, ClientJson.CamelCase);
        }

        private static bool HasNonBlankString(
            IReadOnlyDictionary<string, JsonElement> properties,
            string name)
            => properties.TryGetValue(name, out var value)
               && value.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(value.GetString());

        private static int? ReadPositiveInt(
            IReadOnlyDictionary<string, JsonElement> properties,
            string name)
            => properties.TryGetValue(name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var parsed)
               && parsed > 0
                ? parsed
                : null;
    }
}
