using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static bool TryValidateInitialResearchActionArguments(
        JsonElement arguments,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            failureReason = "object_required";
            return false;
        }

        var invalidFields = new List<string>();
        var capability = ReadCompactFollowUpString(arguments, "capability");
        if (capability is not (
                "rag_search"
                or "documents_navigation"
                or "documents_content_cards"
                or "documents_context"))
        {
            invalidFields.Add("capability");
        }

        foreach (var field in new[]
                 {
                     (Name: "query", MaximumLength: 180),
                     (Name: "scope", MaximumLength: 120),
                     (Name: "document", MaximumLength: 220),
                     (Name: "anchor", MaximumLength: 160)
                 })
        {
            if (!TryGetPropertyIgnoreCase(
                    arguments,
                    field.Name,
                    out var value)
                || value.ValueKind != JsonValueKind.String
                || (value.GetString()?.Length ?? 0) > field.MaximumLength)
            {
                invalidFields.Add(field.Name);
            }
        }

        if (TryGetPropertyIgnoreCase(
                arguments,
                "navigationKind",
                out var navigationKind)
            && (navigationKind.ValueKind != JsonValueKind.String
                || navigationKind.GetString() is not (
                    "" or "navigation_entry" or "title_anchor")))
        {
            invalidFields.Add("navigationKind");
        }

        if (!TryGetPropertyIgnoreCase(arguments, "limit", out var limit)
            || limit.ValueKind != JsonValueKind.Number
            || !limit.TryGetInt32(out var limitValue)
            || limitValue is < 1 or > 120)
        {
            invalidFields.Add("limit");
        }
        if (!TryGetPropertyIgnoreCase(arguments, "offset", out var offset)
            || offset.ValueKind != JsonValueKind.Number
            || !offset.TryGetInt32(out var offsetValue)
            || offsetValue is < 0 or > 10000)
        {
            invalidFields.Add("offset");
        }
        if (TryGetPropertyIgnoreCase(arguments, "mode", out var mode)
            && (mode.ValueKind != JsonValueKind.String
                || mode.GetString() is not (
                    "auto" or "focused" or "balanced" or "broad")))
        {
            invalidFields.Add("mode");
        }
        if (TryGetPropertyIgnoreCase(
                arguments,
                "inventoryMode",
                out var inventoryMode)
            && (inventoryMode.ValueKind != JsonValueKind.String
                || inventoryMode.GetString() is not (
                    "" or "ordered" or "representative")))
        {
            invalidFields.Add("inventoryMode");
        }

        if (invalidFields.Count == 0)
            return true;

        failureReason = "fields_missing_or_invalid:"
            + string.Join(",", invalidFields.Distinct(StringComparer.Ordinal));
        return false;
    }
}
