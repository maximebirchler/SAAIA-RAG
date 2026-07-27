internal static class DoclingDocumentTraversal
{
    public static IReadOnlyDictionary<string, DoclingContentItem> BuildItemMap(
        DoclingDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var items = source.Texts.Cast<DoclingContentItem>()
            .Concat(source.Tables)
            .Concat(source.Pictures)
            .Concat(source.KeyValueItems)
            .Concat(source.FormItems)
            .Concat(source.Groups)
            .Where(static item => !string.IsNullOrWhiteSpace(item.SelfRef))
            .ToArray();
        var duplicates = items
            .GroupBy(static item => item.SelfRef, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicates is not null)
            throw new InvalidDataException($"Docling self reference '{duplicates.Key}' is duplicated.");

        return items.ToDictionary(static item => item.SelfRef, StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<string, int> BuildReadingOrder(
        DoclingDocument source,
        IReadOnlyDictionary<string, DoclingContentItem>? sourceItems = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        sourceItems ??= BuildItemMap(source);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var order = 0;

        void Visit(string sourceRef)
        {
            if (string.IsNullOrWhiteSpace(sourceRef))
                return;
            if (visiting.Contains(sourceRef))
                throw new InvalidDataException(
                    $"Docling tree contains a cycle at '{sourceRef}'.");
            if (result.ContainsKey(sourceRef))
                return;
            if (!sourceItems.TryGetValue(sourceRef, out var item))
                throw new InvalidDataException($"Docling tree reference '{sourceRef}' cannot be resolved.");
            if (!visiting.Add(sourceRef))
                throw new InvalidDataException($"Docling tree contains a cycle at '{sourceRef}'.");

            if (item is not DoclingGroup)
                result[sourceRef] = order++;

            foreach (var child in item.Children)
                Visit(child.Ref);

            visiting.Remove(sourceRef);
        }

        foreach (var child in source.Body.Children)
            Visit(child.Ref);
        foreach (var child in source.Furniture.Children)
            Visit(child.Ref);
        foreach (var item in source.Texts.Cast<DoclingContentItem>()
                     .Concat(source.Tables)
                     .Concat(source.Pictures)
                     .Concat(source.KeyValueItems)
                     .Concat(source.FormItems)
                     .Concat(source.Groups))
        {
            Visit(item.SelfRef);
        }

        return result;
    }

    public static int ResolveOrder(
        IReadOnlyDictionary<string, int> orderByRef,
        string sourceRef,
        int provenanceIndex)
    {
        var order = orderByRef.TryGetValue(sourceRef, out var value)
            ? value
            : (int.MaxValue / 100) - 1;
        return checked((order * 100) + Math.Clamp(provenanceIndex, 0, 99));
    }
}
