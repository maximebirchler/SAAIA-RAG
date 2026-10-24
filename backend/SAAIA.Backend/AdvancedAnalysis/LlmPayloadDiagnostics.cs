using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

/// <summary>Inspects the actual wire projection without logging prompt/evidence text or tool arguments.</summary>
internal static class LlmPayloadDiagnostics
{
    private static readonly string[] Sections = ["request", "load", "evidence", "observations", "researchState",
        "researchWorkspace", "researchTools", "tools", "priorSearches", "candidateInventory", "claimCoordinates", "originalWriterJson"];

    internal static void AnnotateRequest(BackendDiagnostics.Operation probe, string userPrompt, string serializedPayload)
    {
        using var wire = JsonDocument.Parse(serializedPayload);
        if (wire.RootElement.TryGetProperty("tools", out var toolJson) && toolJson.ValueKind != JsonValueKind.Null)
        {
            probe.Set("tools_characters", toolJson.GetRawText().Length);
            probe.Set("tools_count", toolJson.ValueKind == JsonValueKind.Array ? toolJson.GetArrayLength() : (int?)null);
            probe.Set("proposed_tools", ToolNames(toolJson));
        }
        else { probe.Set("tools_characters", 0); probe.Set("tools_count", 0); }
        try
        {
            using var document = JsonDocument.Parse(userPrompt);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { probe.Set("user_payload_shape", "non_object"); return; }
            probe.Set("user_payload_shape", "json_object");
            foreach (var section in Sections)
                if (root.TryGetProperty(section, out var value)) probe.Set("section_" + section + "_characters", value.GetRawText().Length);
            if (root.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array)
            {
                var items = evidence.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.Object).ToArray();
                probe.Set("visible_evidence_count", evidence.GetArrayLength());
                probe.Set("truncated_evidence_count", items.Count(item => item.TryGetProperty("contentTruncated", out var value) && value.ValueKind == JsonValueKind.True));
                probe.Set("visible_evidence_characters", items.Sum(VisibleCharacters));
                probe.Set("visible_evidence_ids", items.Select(item => SafeIdentifier(item, "evidenceId")).Where(static value => value is not null).Take(32).ToArray());
                probe.Set("visible_source_keys", items.Select(item => SafeIdentifier(item, "sourceKey")).Where(static value => value is not null).Distinct(StringComparer.Ordinal).Take(32).ToArray());
                probe.Set("evidence_id_sample_limit", 32);
                probe.Set("evidence_ids_sampled", items.Length > 32);
                probe.Set("evidence_identity_scope", "opaque_handles_join_job_tool_history");
            }
        }
        catch (JsonException) { probe.Set("user_payload_shape", "non_json"); }
    }

    internal static void AnnotateSelectedTools(BackendDiagnostics.Operation probe, string? nativeCallsJson)
    {
        if (nativeCallsJson is null) { probe.Set("selected_tools", Array.Empty<string>()); return; }
        try
        {
            using var document = JsonDocument.Parse(nativeCallsJson);
            probe.Set("selected_tools", ToolNames(document.RootElement));
        }
        catch (JsonException) { probe.Set("selected_tools_diagnostic", "invalid_envelope"); }
    }

    private static string[] ToolNames(JsonElement values)
        => values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(static value => value.ValueKind == JsonValueKind.Object)
            .Select(value => value.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object ? function : value)
            .Select(value => SafeIdentifier(value, "name")).Where(static value => value is not null).Cast<string>()
            .Distinct(StringComparer.Ordinal).Take(32).ToArray() : [];

    private static long VisibleCharacters(JsonElement item)
    {
        long count = item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString()!.Length : 0;
        if (item.TryGetProperty("supportingContentWindows", out var windows) && windows.ValueKind == JsonValueKind.Array)
            count += windows.EnumerateArray().Where(static window => window.ValueKind == JsonValueKind.Object)
                .Sum(window => window.TryGetProperty("content", out var value) && value.ValueKind == JsonValueKind.String ? (long)value.GetString()!.Length : 0);
        return count;
    }

    private static string? SafeIdentifier(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String) return null;
        var text = item.GetString();
        return text is { Length: > 0 and <= 128 } && text.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? text : null;
    }
}
