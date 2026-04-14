using System.Text.Json;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private static string? ReadTrackedJobStatus(JsonElement snapshot)
        => TryGetString(snapshot, "status")
           ?? TryGetString(snapshot, "Status")
           ?? TryGetNestedString(snapshot, "payload", "status");

    private static string? ReadTrackedJobLastError(JsonElement snapshot)
        => TryGetString(snapshot, "lastError")
           ?? TryGetString(snapshot, "LastError")
           ?? TryGetNestedString(snapshot, "payload", "lastError")
           ?? TryGetNestedString(snapshot, "payload", "LastError");

    private static string? ReadTrackedJobProgressPhase(JsonElement snapshot)
        => TryGetString(snapshot, "progressPhase")
           ?? TryGetString(snapshot, "ProgressPhase")
           ?? TryGetNestedString(snapshot, "progress", "phase")
           ?? TryGetNestedString(snapshot, "payload", "progress", "phase");

    private static int? ReadTrackedJobProgressCurrent(JsonElement snapshot)
        => TryGetInt(snapshot, "progressCurrent")
           ?? TryGetInt(snapshot, "ProgressCurrent")
           ?? TryGetNestedInt(snapshot, "progress", "current")
           ?? TryGetNestedInt(snapshot, "payload", "progress", "current");

    private static int? ReadTrackedJobProgressTotal(JsonElement snapshot)
        => TryGetInt(snapshot, "progressTotal")
           ?? TryGetInt(snapshot, "ProgressTotal")
           ?? TryGetNestedInt(snapshot, "progress", "total")
           ?? TryGetNestedInt(snapshot, "payload", "progress", "total");

    private static int? ReadTrackedJobProgressPercent(JsonElement snapshot)
        => TryGetInt(snapshot, "progressPercent")
           ?? TryGetInt(snapshot, "ProgressPercent")
           ?? TryGetNestedInt(snapshot, "progress", "percent")
           ?? TryGetNestedInt(snapshot, "payload", "progress", "percent");

    private static string? ReadTrackedJobDocId(JsonElement snapshot)
        => TryGetString(snapshot, "docId")
           ?? TryGetString(snapshot, "DocId")
           ?? TryGetNestedString(snapshot, "payload", "docId");

    private static string? TryGetNestedString(JsonElement element, params string[] path)
    {
        if (!TryGetNested(element, out var value, path))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? TryGetNestedInt(JsonElement element, params string[] path)
    {
        if (!TryGetNested(element, out var value, path))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;
        return null;
    }

    private static bool TryGetNested(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !TryGetPropertyIgnoreCase(value, segment, out value))
            {
                value = default;
                return false;
            }
        }
        return true;
    }

    private static List<HelpCategory> ParseCategories(JsonElement root)
    {
        var list = new List<HelpCategory>();
        if (!root.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in categories.EnumerateArray())
        {
            var categoryRef = TryGetString(item, "categoryRef") ?? string.Empty;
            var categoryPath = TryGetString(item, "categoryPath") ?? string.Empty;
            var displayName = TryGetString(item, "canonicalName")
                ?? TryGetString(item, "displayName")
                ?? categoryPath
                ?? categoryRef
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(categoryPath))
                categoryPath = !string.IsNullOrWhiteSpace(displayName) ? displayName : categoryRef;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = !string.IsNullOrWhiteSpace(categoryPath) ? categoryPath : categoryRef;

            var documentCount = TryGetInt(item, "documentCount") ?? TryGetInt(item, "totalDocuments") ?? 0;
            var aliases = ReadStringArray(item, "aliases");
            var safeCategoryRef = categoryRef ?? string.Empty;
            var safeCategoryPath = categoryPath ?? string.Empty;
            var safeDisplayName = displayName ?? string.Empty;
            var searchText = string.Join(" ", new[] { safeCategoryRef, safeCategoryPath, safeDisplayName }.Concat(aliases).Where(x => !string.IsNullOrWhiteSpace(x)));
            list.Add(new HelpCategory(safeCategoryRef, safeCategoryPath, safeDisplayName, documentCount, searchText));
        }

        return list;
    }

    private static List<HelpDocument> ParseDocuments(JsonElement root)
    {
        var list = new List<HelpDocument>();
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in items.EnumerateArray())
        {
            var docId = TryGetString(item, "docId") ?? TryGetString(item, "id");
            var docPath = TryGetString(item, "docPath") ?? string.Empty;
            var docName = TryGetString(item, "docName") ?? TryGetString(item, "canonicalName") ?? docPath;
            var categoryPath = TryGetString(item, "categoryPath") ?? string.Empty;
            list.Add(new HelpDocument(docId, docPath, docName, categoryPath));
        }

        return list;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;
        return property.GetString();
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;
        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
            return value;
        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                result.Add(item.GetString()!);
        }

        return result;
    }
}
