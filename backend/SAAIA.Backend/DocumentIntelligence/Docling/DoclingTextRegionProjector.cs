using System.Text;

internal static class DoclingTextRegionProjector
{
    internal static (string Raw, string Canonical) Project(
        DoclingTextItem item,
        DoclingProvenance provenance,
        int provenanceIndex)
    {
        if (item.Provenance.Count <= 1)
        {
            var singleRegionRaw = string.IsNullOrEmpty(item.OriginalText)
                ? item.Text ?? ""
                : item.OriginalText;
            return (singleRegionRaw, item.Text ?? "");
        }

        if (provenance.CharacterSpan is not { Length: 2 })
        {
            throw new InvalidDataException(
                $"Docling text '{item.SelfRef}' provenance {provenanceIndex} "
                + "does not contain an exact two-bound character span.");
        }

        var start = provenance.CharacterSpan[0];
        var endExclusive = provenance.CharacterSpan[1];
        var fragment = SliceByUnicodeScalarRange(
            item.OriginalText ?? "",
            start,
            endExclusive,
            item.SelfRef,
            provenanceIndex);

        // Docling defines multi-provenance charspans against `orig`, not `text`.
        // Its own serializer therefore uses the exact `orig` slice as both values.
        return (fragment, fragment);
    }

    private static string SliceByUnicodeScalarRange(
        string value,
        int start,
        int endExclusive,
        string sourceRef,
        int provenanceIndex)
    {
        if (start < 0 || endExclusive < start)
        {
            throw InvalidCharacterSpan(
                sourceRef,
                provenanceIndex,
                start,
                endExclusive,
                value.EnumerateRunes().Count());
        }

        var scalarIndex = 0;
        var utf16Index = 0;
        int? utf16Start = start == 0 ? 0 : null;
        int? utf16End = endExclusive == 0 ? 0 : null;
        foreach (var rune in value.EnumerateRunes())
        {
            if (scalarIndex == start)
                utf16Start = utf16Index;
            if (scalarIndex == endExclusive)
            {
                utf16End = utf16Index;
                break;
            }

            utf16Index += rune.Utf16SequenceLength;
            scalarIndex++;
        }

        if (scalarIndex == start)
            utf16Start ??= utf16Index;
        if (scalarIndex == endExclusive)
            utf16End ??= utf16Index;
        if (!utf16Start.HasValue || !utf16End.HasValue)
        {
            throw InvalidCharacterSpan(
                sourceRef,
                provenanceIndex,
                start,
                endExclusive,
                scalarIndex);
        }

        return value.Substring(
            utf16Start.Value,
            utf16End.Value - utf16Start.Value);
    }

    private static InvalidDataException InvalidCharacterSpan(
        string sourceRef,
        int provenanceIndex,
        int start,
        int endExclusive,
        int scalarLength)
        => new(
            $"Docling text '{sourceRef}' provenance {provenanceIndex} "
            + $"contains invalid character span [{start}, {endExclusive}) "
            + $"for original text length {scalarLength}.");

}
