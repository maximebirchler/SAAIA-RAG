using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private JsonElement ExecExportCreate(JsonElement args)
    {
        var format = args.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? (f.GetString() ?? "txt") : "txt";
        var fileName = args.TryGetProperty("fileName", out var n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "export") : args.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "export") : "export";
        var content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "") : "";

        var path = ExportService.Create(format, fileName, content);
        var payload = new { savedPath = path };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecSupportBundleAsync(JsonElement args, CancellationToken ct)
    {
        if (_settings is null)
            return JsonDocument.Parse("{\"error\":\"missing_settings\"}").RootElement;

        var include = GetStringArrayArg(args, "include");
        var runtimeSnapshot = BuildAgentRuntimeSnapshot();
        var zip = await SupportBundleBuilder.BuildAsync(_settings, runtimeSnapshot, include).ConfigureAwait(false);
        var payload = new
        {
            zipPath = zip,
            included = include is { Count: > 0 } ? include.ToArray() : new[] { "diagnostics/agent-runtime" }
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecRagDebugScrollAsync(JsonElement args, CancellationToken ct)
    {
        var cursor = args.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var limit = args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 100;
        var docId = GetStringArg(args, "docId");
        var category = NormalizeCategoryPathArg(GetStringArg(args, "category") ?? GetStringArg(args, "categoryPath"));
        var pageStart = GetIntArg(args, "pageStart") ?? GetIntArg(args, "page_start");
        var pageEnd = GetIntArg(args, "pageEnd") ?? GetIntArg(args, "page_end");
        var chunkType = GetStringArg(args, "chunkType") ?? GetStringArg(args, "chunk_type");
        var contentRole = GetStringArg(args, "contentRole") ?? GetStringArg(args, "content_role");
        string? docPath = null;
        if (args.TryGetProperty("docRef", out var dref) && dref.ValueKind == JsonValueKind.String)
        {
            var resolved = await ResolveDocRefAsync(dref.GetString() ?? string.Empty, ct).ConfigureAwait(false);
            docPath = resolved?.DocPath;
            docId ??= resolved?.DocId;
        }
        else if (args.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String)
        {
            docPath = dp.GetString();
        }

        try
        {
            var raw = await _api.RagDebugScrollAsync(
                    cursor,
                    limit,
                    docPath,
                    ct,
                    docId,
                    category,
                    pageStart,
                    pageEnd,
                    chunkType,
                    contentRole)
                .ConfigureAwait(false);
            return raw;
        }
        catch
        {
            return JsonDocument.Parse("{\"items\":[],\"nextCursor\":null,\"error\":\"not_supported\"}").RootElement;
        }
    }
}
