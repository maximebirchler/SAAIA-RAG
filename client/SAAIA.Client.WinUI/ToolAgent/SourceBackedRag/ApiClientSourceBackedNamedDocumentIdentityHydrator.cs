using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal sealed class SourceBackedCanonicalIdentityHydrationException
    : Exception
{
    public SourceBackedCanonicalIdentityHydrationException(
        string reasonCode,
        string message)
        : base(message)
        => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}

/// <summary>
/// Reads the active document header from the user-safe context endpoint. The
/// response's context items and text are deliberately ignored.
/// </summary>
public sealed class ApiClientSourceBackedNamedDocumentIdentityHydrator
    : ISourceBackedNamedDocumentIdentityHydrator
{
    private const string NotFoundReason = "canonical_identity_not_found";
    private const string MismatchReason = "canonical_identity_mismatch";
    private const string IncompleteReason = "canonical_identity_incomplete";
    private const string ObservationFailedReason =
        "canonical_identity_observation_failed";

    private readonly ApiClient _api;

    public ApiClientSourceBackedNamedDocumentIdentityHydrator(ApiClient api)
        => _api = api ?? throw new ArgumentNullException(nameof(api));

    public async Task<SourceBackedDocumentResolutionCandidate> HydrateAsync(
        SourceBackedDocumentResolutionCandidate candidate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var payload = await _api.DocumentsContextAsync(
                candidate.DocId,
                docPath: null,
                chunkId: null,
                pageStart: null,
                pageEnd: null,
                before: 0,
                after: 0,
                limit: 1,
                offset: 0,
                ct)
            .ConfigureAwait(false);

        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetProperty(payload, "found", out var foundNode)
            || foundNode.ValueKind is not JsonValueKind.True
                and not JsonValueKind.False)
        {
            throw Failure(
                ObservationFailedReason,
                "The canonical identity response has no valid found flag.");
        }
        if (!foundNode.GetBoolean())
        {
            throw Failure(
                NotFoundReason,
                "The exact catalog identity has no active document header.");
        }
        if (!TryGetProperty(payload, "document", out var document)
            || document.ValueKind != JsonValueKind.Object)
        {
            throw Failure(
                ObservationFailedReason,
                "The canonical identity response has no document header.");
        }

        var docId = ReadString(document, "docId");
        var docPath = ReadString(document, "docPath");
        var docName = ReadString(document, "docName");
        var revisionId = ReadString(document, "revisionId");
        var sourceHash = ReadString(document, "sourceHash");
        if (string.IsNullOrWhiteSpace(docId)
            || string.IsNullOrWhiteSpace(docPath)
            || string.IsNullOrWhiteSpace(docName))
        {
            throw Failure(
                IncompleteReason,
                "The active document header has no complete base identity.");
        }
        if (!SameIdentity(candidate.DocId, docId)
            || !SamePath(candidate.DocPath, docPath)
            || !SameName(candidate.DocName, docName))
        {
            throw Failure(
                MismatchReason,
                "The active document header differs from the exact catalog identity.");
        }
        if (!Guid.TryParse(revisionId, out var parsedRevision)
            || parsedRevision == Guid.Empty
            || !IsSha256(sourceHash))
        {
            throw Failure(
                IncompleteReason,
                "The active document header has no valid revision identity.");
        }

        return new SourceBackedDocumentResolutionCandidate(
            docId.Trim(),
            docPath.Trim(),
            docName.Trim(),
            parsedRevision.ToString("D"),
            sourceHash!.Trim().ToLowerInvariant());
    }

    private static SourceBackedCanonicalIdentityHydrationException Failure(
        string reasonCode,
        string message)
        => new(reasonCode, message);

    private static bool TryGetProperty(
        JsonElement obj,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement obj, string propertyName)
        => TryGetProperty(obj, propertyName, out var node)
           && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static bool SameIdentity(string expected, string actual)
        => string.Equals(
            Clean(expected),
            Clean(actual),
            StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string expected, string actual)
        => string.Equals(
            NormalizePath(expected),
            NormalizePath(actual),
            StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string expected, string actual)
        => string.Equals(
            Clean(expected),
            Clean(actual),
            StringComparison.OrdinalIgnoreCase);

    private static string Clean(string? value)
        => (value ?? string.Empty)
            .Trim()
            .Normalize(NormalizationForm.FormKC);

    private static string NormalizePath(string? value)
        => Clean(value)
            .Replace('\\', '/')
            .Trim('/');

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character =>
               character is >= '0' and <= '9'
                   or >= 'a' and <= 'f'
                   or >= 'A' and <= 'F');
}
