using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

/// <summary>Mandatory scope is separate from revisable LLM research hypotheses. Backend ACLs remain authoritative.</summary>
internal static class SourceBackedResearchScopePolicy
{
    internal static string? ValidateMandatoryScope(SourceBackedIntake intake, string tool, JsonElement arguments, EvidenceBundle? evidence = null)
    {
        if (intake.RequiredCategoryPaths is not { Count: > 0 }) return null;
        if (arguments.ValueKind != JsonValueKind.Object) return "mandatory_category_scope_arguments_invalid";
        bool Allowed(string? path) => !string.IsNullOrWhiteSpace(path) && intake.RequiredCategoryPaths.Any(required =>
            string.Equals(path, required, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(required.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        if (tool == "documents.context_batch")
        {
            if (!arguments.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array)
                return "mandatory_category_scope_document_unresolved";
            return targets.EnumerateArray().Select(target => ValidateMandatoryScope(intake, "documents.context", target, evidence))
                .FirstOrDefault(error => error is not null);
        }
        if (tool == "documents.context")
        {
            string? Read(string key) => arguments.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            var id = Read("docId"); var path = Read("docPath"); var reference = Read("docRef");
            var matches = (evidence?.Items ?? Array.Empty<EvidenceItem>()).Where(item =>
                !string.IsNullOrWhiteSpace(id) ? string.Equals(item.DocId, id, StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrWhiteSpace(path) ? string.Equals(item.DocPath, path, StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrWhiteSpace(reference) && (string.Equals(item.DocPath, reference, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.DocName, reference, StringComparison.OrdinalIgnoreCase))).ToArray();
            return matches.Length == 0 || matches.Any(item => !Allowed(item.CategoryPath))
                ? "mandatory_category_scope_document_unresolved_or_outside" : null;
        }
        if (tool is not ("rag.search" or "rag.multi_search" or "documents.navigation" or "documents.content_cards")) return null;
        var scope = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("categoryPath", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        return Allowed(scope) ? null : "mandatory_category_scope_required";
    }
}
