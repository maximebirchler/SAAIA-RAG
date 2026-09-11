using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IEnumerable<RouterPlan.ToolCall> SanitizeToolCalls(IReadOnlyList<RouterPlan.ToolCall>? toolCalls)
    {
        if (toolCalls is null)
            yield break;

        var ragCalls = 0;
        foreach (var call in toolCalls)
        {
            if (call is null || string.IsNullOrWhiteSpace(call.Name))
                continue;

            var normalizedName = NormalizeToolName(call.Name);
            if (!ToolManifest.IsKnownTool(normalizedName))
                continue;

            if (IsRagToolName(normalizedName))
            {
                ragCalls++;
                if (ragCalls > MaxRagToolCalls)
                    continue;
            }

            yield return new RouterPlan.ToolCall
            {
                Name = normalizedName,
                Args = NormalizeToolArgs(normalizedName, call.Args),
                QueryHint = string.IsNullOrWhiteSpace(call.QueryHint)
                    ? null
                    : call.QueryHint.Trim()
            };
        }
    }

    private static string NormalizeToolName(string? toolName)
    {
        var normalized = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "documents.catalog" => "documents.list",
            "documents.find" => "documents.search",
            "diagnostic.latency" => "diagnostic.performance",
            "support.zip" or "support.export" => "support.bundle",
            "summary.read" => "summary.get",
            "summary.missing" or "summaries.missing" => "summary.status.list",
            "summary.missing_count" or "summaries.missing_count" => "summary.status.count",
            _ => normalized
        };
    }

    private static JsonElement NormalizeToolArgs(string toolName, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return JsonDocument.Parse("{}").RootElement.Clone();

        object payload = toolName switch
        {
            "documents.list" => new
            {
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef"),
                changedSince = NormalizeChangedSinceArg(GetStringArg(args, "changedSince")),
                q = GetStringArg(args, "q") ?? GetStringArg(args, "query"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 80, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.search" => new
            {
                q = (GetStringArg(args, "q") ?? GetStringArg(args, "query") ?? string.Empty).Trim(),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 80, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.count" => new
            {
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef"),
                q = GetStringArg(args, "q") ?? GetStringArg(args, "query")
            },
            "documents.categories" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.tree" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef"),
                depth = NormalizeIntArg(GetIntArg(args, "depth"), 8, 1, 20),
                format = NormalizeTreeFormat(GetStringArg(args, "format")),
                limit = NormalizeNullableIntArg(GetIntArg(args, "limit"), 1, 5000),
                offset = NormalizeNullableIntArg(GetIntArg(args, "offset"), 0, 100000)
            },
            "documents.navigation" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef"),
                docRef = GetDocRefFromArgs(args),
                docPath = NormalizeCategoryPathArg(GetStringArg(args, "docPath")),
                q = GetStringArg(args, "q") ?? GetStringArg(args, "query"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 120, 1, 200),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.context" => new
            {
                docRef = GetDocRefFromArgs(args),
                docId = GetStringArg(args, "docId"),
                docPath = NormalizeCategoryPathArg(GetStringArg(args, "docPath")),
                chunkId = GetStringArg(args, "chunkId") ?? GetStringArg(args, "chunk_id"),
                pageStart = NormalizeNullableIntArg(GetIntArg(args, "pageStart") ?? GetIntArg(args, "page_start"), 1, 100000),
                pageEnd = NormalizeNullableIntArg(GetIntArg(args, "pageEnd") ?? GetIntArg(args, "page_end"), 1, 100000),
                before = NormalizeIntArg(GetIntArg(args, "before"), 2, 0, 20),
                after = NormalizeIntArg(GetIntArg(args, "after"), 4, 0, 30),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 12, 1, 50),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.stats" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef")
            },
            "documents.empty_count" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath"))
            },
            "documents.empty_list" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 200, 1, 2000),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.extraction_quality" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 200, 1, 2000)
            },
            "documents.extraction_pages" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty
            },
            "sources.resolve" => new
            {
                @ref = (GetStringArg(args, "ref") ?? GetStringArg(args, "pdfRef") ?? string.Empty).Trim()
            },
            "rag.search" => new
            {
                query = (GetStringArg(args, "query") ?? string.Empty).Trim(),
                topK = NormalizeIntArg(GetIntArg(args, "topK"), 8, 1, 20),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category")),
                categoryRef = GetStringArg(args, "categoryRef") ?? GetNestedStringArg(args, "filters", "categoryRef"),
                docId = GetRagDocIdArg(args),
                docPath = GetRagDocPathArg(args),
                maxPerDoc = NormalizeNullableIntArg(GetRagMaxPerDocArg(args), 1, 20),
                maxPerPage = NormalizeNullableIntArg(GetRagMaxPerPageArg(args), 1, 20),
                pageStart = NormalizeNullableIntArg(GetRagPageStartArg(args), 1, 100000),
                pageEnd = NormalizeNullableIntArg(GetRagPageEndArg(args), 1, 100000),
                mode = NormalizeRagMode(GetStringArg(args, "mode")),
                researchMode = GetRagResearchModeArg(args),
                includeResearchSurfaces = GetRagIncludeResearchSurfacesArg(args)
            },
            "rag.multi_search" => new
            {
                queries = NormalizeRagMultiSearchQueries(args),
                topK = NormalizeIntArg(GetIntArg(args, "topK"), 8, 1, 20),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category")),
                categoryRef = GetStringArg(args, "categoryRef") ?? GetNestedStringArg(args, "filters", "categoryRef"),
                docId = GetRagDocIdArg(args),
                docPath = GetRagDocPathArg(args),
                maxPerDoc = NormalizeNullableIntArg(GetRagMaxPerDocArg(args), 1, 20),
                maxPerPage = NormalizeNullableIntArg(GetRagMaxPerPageArg(args), 1, 20),
                pageStart = NormalizeNullableIntArg(GetRagPageStartArg(args), 1, 100000),
                pageEnd = NormalizeNullableIntArg(GetRagPageEndArg(args), 1, 100000),
                mode = NormalizeRagMode(GetStringArg(args, "mode")),
                researchMode = GetRagResearchModeArg(args),
                includeResearchSurfaces = GetRagIncludeResearchSurfacesArg(args)
            },
            "rag.summarize_live" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty,
                level = NormalizeLiveSummaryLevel(GetStringArg(args, "level")),
                strategy = NormalizeSummaryStrategy(GetStringArg(args, "strategy")),
                language = NormalizeSummaryLanguage(GetStringArg(args, "language")),
                responseLanguage = NormalizeSummaryLanguage(GetStringArg(args, "responseLanguage") ?? GetStringArg(args, "language")),
                docLanguage = NormalizeDocumentLanguageTag(GetStringArg(args, "docLanguage")),
                maxWords = NormalizeNullableIntArg(GetIntArg(args, "maxWords"), 20, 1200),
                maxChunks = NormalizeNullableIntArg(GetIntArg(args, "maxChunks"), 1, 40),
                maxBatches = NormalizeNullableIntArg(GetIntArg(args, "maxBatches"), 1, 8),
                maxCharsPerBatch = NormalizeNullableIntArg(GetIntArg(args, "maxCharsPerBatch"), 1000, 12000),
                userRequest = GetStringArg(args, "userRequest"),
                overviewFacets = NormalizeEvidenceOverviewFacets(args),
                requestedPointCount = NormalizeNullableIntArg(GetIntArg(args, "requestedPointCount"), 2, MaximumEvidenceOverviewPointCount),
                sampleCount = NormalizeNullableIntArg(GetIntArg(args, "sampleCount"), 2, MaximumEvidenceOverviewPointCount + 1)
            },
            "summary.get" or "summary.exists" or "admin.summary.request" or "admin.summary.generate" or "admin.summary.delete" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty,
                level = "medium",
                force = GetBoolArg(args, "force") ?? false
            },
            "summary.search" => new
            {
                q = (GetStringArg(args, "q") ?? string.Empty).Trim(),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 20, 1, 200),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "summary.status.count" => new
            {
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef")
            },
            "summary.status.list" or "admin.summary.missing" => new
            {
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef")
            },
            "admin.summary.submit" => new
            {
                jobId = GetStringArg(args, "jobId"),
                executionLeaseToken = GetStringArg(args, "executionLeaseToken") ?? GetStringArg(args, "leaseToken"),
                docRef = GetDocRefFromArgs(args) ?? string.Empty,
                level = "medium",
                docLanguage = NormalizeAdminSummarySubmitDocLanguage(GetStringArg(args, "docLanguage")),
                sourceHash = GetStringArg(args, "sourceHash") ?? string.Empty,
                summaryText = GetStringArg(args, "summaryText") ?? GetStringArg(args, "content") ?? string.Empty,
                meta = TryGetObjectArg(args, "meta")
            },
            "admin.summary.status" or "admin.jobs.cancel" => new
            {
                jobId = GetStringArg(args, "jobId") ?? string.Empty
            },
            "admin.ingestion.reindex" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty
            },
            "admin.jobs.list" => new
            {
                type = GetStringArg(args, "type"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "admin.audit" => new
            {
                action = GetStringArg(args, "action"),
                target = GetStringArg(args, "target"),
                since = NormalizeChangedSinceArg(GetStringArg(args, "since")),
                until = NormalizeChangedSinceArg(GetStringArg(args, "until")),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 1000),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "diagnostic.performance" => new
            {
                lastN = NormalizeNullableIntArg(GetIntArg(args, "lastN"), 1, 50)
            },
            "export.create" => new
            {
                format = NormalizeExportFormat(GetStringArg(args, "format")),
                fileName = (GetStringArg(args, "fileName") ?? GetStringArg(args, "title") ?? "export").Trim(),
                content = GetStringArg(args, "content") ?? string.Empty
            },
            "support.bundle" => new
            {
                include = GetStringArrayArg(args, "include")?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>()
            },
            "rag.debug.scroll" => new
            {
                docRef = GetDocRefFromArgs(args),
                docId = GetStringArg(args, "docId"),
                docPath = GetStringArg(args, "docPath"),
                category = NormalizeCategoryPathArg(GetStringArg(args, "category") ?? GetStringArg(args, "categoryPath")),
                cursor = GetStringArg(args, "cursor"),
                pageStart = NormalizeNullableIntArg(GetIntArg(args, "pageStart") ?? GetIntArg(args, "page_start"), 1, 100000),
                pageEnd = NormalizeNullableIntArg(GetIntArg(args, "pageEnd") ?? GetIntArg(args, "page_end"), 1, 100000),
                chunkType = GetStringArg(args, "chunkType") ?? GetStringArg(args, "chunk_type"),
                contentRole = GetStringArg(args, "contentRole") ?? GetStringArg(args, "content_role"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500)
            },
            _ => JsonSerializer.Deserialize<object>(args.GetRawText()) ?? new { }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
    }

    private static int NormalizeIntArg(int? value, int fallback, int min, int max)
        => Math.Clamp(value ?? fallback, min, max);

    private static int? NormalizeNullableIntArg(int? value, int min, int max)
        => value.HasValue ? Math.Clamp(value.Value, min, max) : null;

    private static string? NormalizeCategoryPathArg(string? categoryPath)
    {
        var normalized = (categoryPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeChangedSinceArg(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTimeOffset.TryParse(raw.Trim(), out var parsed)
            ? parsed.ToUniversalTime().ToString("O")
            : null;
    }

    private static string NormalizeTreeFormat(string? format)
        => string.Equals((format ?? string.Empty).Trim(), "json", StringComparison.OrdinalIgnoreCase) ? "json" : "markdown";

    private static string NormalizeRagMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "focused" or "balanced" or "broad" or "standard" or "strict" ? normalized : "auto";
    }

    private static string NormalizeLiveSummaryLevel(string? level)
    {
        var normalized = (level ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "short" or "medium" or "long" ? normalized : "medium";
    }

    private static string NormalizeSummaryStrategy(string? strategy)
    {
        var normalized = (strategy ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "about" or "summary" or "store" or "evidence_overview" ? normalized : "summary";
    }

    private static string NormalizeSummaryLanguage(string? language)
    {
        var normalized = (language ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "fr" or "en" or "es" or "pt" or "de" or "it" ? normalized : "auto";
    }

    private static string NormalizeAdminSummarySubmitDocLanguage(string? language)
        => NormalizeDocumentLanguageTag(language);

    private static string NormalizeDocumentLanguageTag(string? language)
    {
        var normalized = (language ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized.Contains(',', StringComparison.Ordinal))
            normalized = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        if (normalized.Contains('+', StringComparison.Ordinal))
            normalized = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

        return IsPlausibleDocumentLanguageTag(normalized) ? normalized : "und";
    }

    private static bool IsPlausibleDocumentLanguageTag(string language)
    {
        if (string.Equals(language, "und", StringComparison.Ordinal))
            return true;
        if (string.IsNullOrWhiteSpace(language) || language.Length is < 2 or > 35)
            return false;

        var parts = language.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > 5)
            return false;
        if (parts[0].Length is < 2 or > 8 || !parts[0].All(char.IsLetter))
            return false;

        return parts.Skip(1).All(static part =>
            part.Length is >= 2 and <= 8
            && part.All(static ch => char.IsLetterOrDigit(ch)));
    }

    private static string NormalizeExportFormat(string? format)
    {
        var normalized = (format ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "txt" or "md" or "csv" or "docx" ? normalized : "txt";
    }

    private static string? GetDocRefFromArgs(JsonElement args)
        => GetStringArg(args, "docRef")
           ?? GetStringArg(args, "docId")
           ?? GetStringArg(args, "docPath")
           ?? GetStringArg(args, "ref")
           ?? GetStringArg(args, "pdfRef");

    private static string? GetNestedStringArg(JsonElement args, string parent, string child)
    {
        if (!args.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        return GetStringArg(p, child);
    }

    private static object? TryGetObjectArg(JsonElement args, string propertyName)
    {
        if (!args.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return JsonSerializer.Deserialize<object>(value.GetRawText());
    }
}
