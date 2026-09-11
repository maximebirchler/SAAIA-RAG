using System.IO;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static class SourceBackedAgentToolArgumentNormalizer
{
    public static JsonElement NormalizeDocumentLocator(
        JsonElement arguments,
        EvidenceBundle? bundle = null)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return arguments.Clone();

        var properties = arguments
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        var docId = ReadString(properties, "docId");
        var docRef = ReadString(properties, "docRef");
        var docPath = ReadString(properties, "docPath");
        var explicitDocumentReference = LooksLikeDocumentReference(docPath)
            ? docPath
            : LooksLikeDocumentReference(docRef)
              && !LooksLikeReadOnlySourceAlias(docRef)
              && !LooksLikeEvidenceId(docRef)
                ? docRef
                : null;
        var concreteDocumentPath =
            LooksLikeConcreteDocumentPath(explicitDocumentReference)
                ? explicitDocumentReference
                : null;
        var evidence = string.IsNullOrWhiteSpace(explicitDocumentReference)
            ? ResolveEvidenceReference(bundle, docId)
              ?? ResolveEvidenceReference(bundle, docRef)
            : null;

        if (!string.IsNullOrWhiteSpace(explicitDocumentReference))
        {
            if (!string.IsNullOrWhiteSpace(concreteDocumentPath))
            {
                SetOrRemove(properties, "docPath", concreteDocumentPath);
            }
            else
            {
                properties.Remove("docPath");
                SetOrRemove(properties, "docRef", explicitDocumentReference);
            }
            if (LooksLikeReadOnlySourceAlias(docId)
                || LooksLikeEvidenceId(docId))
            {
                properties.Remove("docId");
                properties.Remove("chunkId");
            }
        }
        else if (evidence is not null)
        {
            SetOrRemove(properties, "docId", evidence.DocId);
            SetOrRemove(
                properties,
                "docPath",
                evidence.DocPath ?? evidence.DocName);
            SetOrRemove(properties, "chunkId", evidence.ChunkId);
            if (evidence.PageStart is > 0)
            {
                properties["pageStart"] =
                    JsonSerializer.SerializeToElement(evidence.PageStart.Value);
                properties["pageEnd"] =
                    JsonSerializer.SerializeToElement(
                        evidence.PageEnd ?? evidence.PageStart.Value);
            }

            if (LooksLikeReadOnlySourceAlias(docRef)
                || LooksLikeEvidenceId(docRef))
            {
                properties.Remove("docRef");
            }
        }
        else if (string.IsNullOrWhiteSpace(docPath)
                 && LooksLikeConcreteDocumentPath(docId))
        {
            properties["docPath"] = JsonSerializer.SerializeToElement(docId);
            properties.Remove("docId");
        }
        else if (string.IsNullOrWhiteSpace(docPath)
                 && LooksLikeDocumentReference(docId))
        {
            properties["docRef"] = JsonSerializer.SerializeToElement(docId);
            properties.Remove("docId");
        }
        else if (LooksLikeReadOnlySourceAlias(docId))
        {
            properties.Remove("docId");
        }
        if (LooksLikeReadOnlySourceAlias(docRef))
            properties.Remove("docRef");

        return JsonSerializer.SerializeToElement(properties, ClientJson.CamelCase);
    }

    private static EvidenceItem? ResolveEvidenceReference(
        EvidenceBundle? bundle,
        string? value)
        => bundle is not null
           && LooksLikeEvidenceId(value)
           && bundle.ById.TryGetValue(value!, out var item)
            ? item
            : null;

    private static void SetOrRemove(
        IDictionary<string, JsonElement> properties,
        string name,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            properties.Remove(name);
        else
            properties[name] = JsonSerializer.SerializeToElement(value.Trim());
    }

    private static string? ReadString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name)
        => properties.TryGetValue(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool LooksLikeDocumentReference(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (LooksLikeConcreteDocumentPath(value)
               || !string.IsNullOrWhiteSpace(Path.GetExtension(value.Trim())));

    private static bool LooksLikeConcreteDocumentPath(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.Contains('/') || value.Contains('\\'));

    private static bool LooksLikeReadOnlySourceAlias(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length is >= 2 and <= 5
           && value[0] is 'D' or 'd'
           && value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool LooksLikeEvidenceId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length is >= 2 and <= 8
           && value[0] is 'E' or 'e'
           && value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
}
