using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string? GetRagCategoryScopeArg(JsonElement args)
        => NormalizeCategoryPathArg(
            GetStringArg(args, "categoryPath")
            ?? GetNestedStringArg(args, "filters", "categoryPath")
            ?? GetStringArg(args, "categoryRef")
            ?? GetNestedStringArg(args, "filters", "categoryRef")
            ?? GetStringArg(args, "category")
            ?? GetNestedStringArg(args, "filters", "category"));

    private static string? GetRagDocIdArg(JsonElement args)
        => NullIfWhiteSpace(
            GetStringArg(args, "docId")
            ?? GetNestedStringArg(args, "filters", "docId"));

    private static string? GetRagDocPathArg(JsonElement args)
        => NullIfWhiteSpace(
            GetStringArg(args, "docPath")
            ?? GetNestedStringArg(args, "filters", "docPath"));

    private static int? GetRagMaxPerDocArg(JsonElement args)
        => GetIntArg(args, "maxPerDoc")
           ?? GetNestedIntArg(args, "filters", "maxPerDoc")
           ?? GetNestedIntArg(args, "diversity", "maxPerDoc");

    private static int? GetRagMaxPerPageArg(JsonElement args)
        => GetIntArg(args, "maxPerPage")
           ?? GetNestedIntArg(args, "filters", "maxPerPage")
           ?? GetNestedIntArg(args, "diversity", "maxPerPage");

    private static int? GetRagPageStartArg(JsonElement args)
        => GetIntArg(args, "pageStart")
           ?? GetNestedIntArg(args, "filters", "pageStart")
           ?? GetIntArg(args, "page")
           ?? GetNestedIntArg(args, "filters", "page");

    private static int? GetRagPageEndArg(JsonElement args)
    {
        var explicitEnd = GetIntArg(args, "pageEnd")
                          ?? GetNestedIntArg(args, "filters", "pageEnd");
        return explicitEnd ?? GetRagPageStartArg(args);
    }

    private static string? GetRagResearchModeArg(JsonElement args)
        => NormalizeRagResearchModeArg(
            GetStringArg(args, "researchMode")
            ?? GetStringArg(args, "research_mode")
            ?? GetNestedStringArg(args, "filters", "researchMode")
            ?? GetNestedStringArg(args, "filters", "research_mode"));

    private static bool? GetRagIncludeResearchSurfacesArg(JsonElement args)
        => GetBoolArg(args, "includeResearchSurfaces")
           ?? GetBoolArg(args, "include_research_surfaces")
           ?? GetNestedBoolArg(args, "filters", "includeResearchSurfaces")
           ?? GetNestedBoolArg(args, "filters", "include_research_surfaces");

    private static bool GetRagTrustCategoryScopeArg(JsonElement args)
        => GetBoolArg(args, "trustCategoryScope")
           ?? GetBoolArg(args, "trust_category_scope")
           ?? GetNestedBoolArg(args, "filters", "trustCategoryScope")
           ?? GetNestedBoolArg(args, "filters", "trust_category_scope")
           ?? false;

    private static bool GetRagDisableAutomaticCategoryScopingArg(JsonElement args)
        => GetBoolArg(args, "disableAutomaticCategoryScoping")
           ?? GetBoolArg(args, "disable_automatic_category_scoping")
           ?? GetNestedBoolArg(args, "filters", "disableAutomaticCategoryScoping")
           ?? GetNestedBoolArg(args, "filters", "disable_automatic_category_scoping")
           ?? false;

    private static bool GetRagSourceBackedCanonicalArg(JsonElement args)
        => GetBoolArg(args, "sourceBackedCanonical")
           ?? GetBoolArg(args, "source_backed_canonical")
           ?? GetNestedBoolArg(args, "filters", "sourceBackedCanonical")
           ?? GetNestedBoolArg(args, "filters", "source_backed_canonical")
           ?? false;

    private static string? NormalizeRagResearchModeArg(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "none" or "default" => null,
            "research" or "exploration" or "broad_exploration" or "source_exploration" or "evidence_exploration" => normalized,
            _ => null
        };
    }

    private string? GetPreferredDocRef(JsonElement args)
        => GetStringArg(args, "docRef")
           ?? GetStringArg(args, "docId")
           ?? GetStringArg(args, "ref")
           ?? _mem.LastRequestedDocumentRef
           ?? _mem.LastFocusedDocument?.DocId
           ?? _mem.LastFocusedDocument?.DocPath
           ?? _mem.LastFocusedDocument?.DocName;

    private static string? GetStringArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetIntArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static int? GetNestedIntArg(JsonElement args, string parent, string child)
    {
        if (!args.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object)
            return null;

        return GetIntArg(p, child);
    }

    private static bool? GetBoolArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static bool? GetNestedBoolArg(JsonElement args, string parent, string child)
    {
        if (!args.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object)
            return null;

        return GetBoolArg(p, child);
    }

    private static List<string>? GetStringArrayArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s.Trim());
            }
            return list;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        return null;
    }
}
