using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<JsonElement> ExecDocumentsListAsync(JsonElement args, CancellationToken ct)
    {
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var q = args.TryGetProperty("q", out var qj) && qj.ValueKind != JsonValueKind.Null ? qj.GetString() : null;
        var changedSince = ParseChangedSinceArg(GetStringArg(args, "changedSince"));
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsListAsync(categoryPath, categoryRef, q, changedSince, limit, offset, ct);
        res = ApplySpecificDocumentQueryGuard(res, q);

        _mem.LastListCategoryPath = categoryPath;
        _mem.LastListQuery = q;

        // Sanitize against filesystem + update PDFxx mapping (robust against moves/renames)
        var sanitized = DocumentListHelper.Sanitize(res, _mem);
        if (sanitized.docs.Count == 0)
        {
            _mem.LastFocusedDocument = null;
            _mem.LastRequestedDocumentRef = null;
        }

        return res;
    }

    private static DateTimeOffset? ParseChangedSinceArg(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTimeOffset.TryParse(raw.Trim(), out var parsed)
            ? parsed
            : null;
    }

    private async Task<JsonElement> ExecDocumentsSearchAsync(JsonElement args, CancellationToken ct)
    {
        var q = args.GetProperty("q").GetString() ?? "";
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsSearchAsync(q, categoryPath, categoryRef, limit, offset, ct);
        res = ApplySpecificDocumentQueryGuard(res, q);

        _mem.LastListCategoryPath = categoryPath;
        _mem.LastListQuery = q;

        var sanitized = DocumentListHelper.Sanitize(res, _mem);
        if (sanitized.docs.Count == 0)
        {
            _mem.LastFocusedDocument = null;
            _mem.LastRequestedDocumentRef = null;
        }

        return res;
    }

    private async Task<JsonElement> ExecDocumentsGetAsync(JsonElement args, CancellationToken ct)
    {
        return await ExecDocumentsGetResolvedAsync(args, ct);
    }
}
