using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services;

public sealed partial class ApiClient
{
    public async Task<JsonElement> SourceResolveAsync(string? refValue, string? pdfRef, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new SourceResolveRequest
        {
            Ref = string.IsNullOrWhiteSpace(refValue) ? null : refValue.Trim(),
            PdfRef = string.IsNullOrWhiteSpace(pdfRef) ? null : pdfRef.Trim()
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Post, "/sources/resolve", body), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async Task<JsonElement> ResolveCategoryAsync(string? path, string? categoryRef, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new ResolveCategoryRequest
        {
            Path = string.IsNullOrWhiteSpace(path) ? null : path.Trim(),
            CategoryRef = string.IsNullOrWhiteSpace(categoryRef) ? null : categoryRef.Trim()
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Post, "/documents/resolve-category", body), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
