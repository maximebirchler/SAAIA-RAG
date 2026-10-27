using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisNativeObservationDescriptor(
    string DocId, string RevisionId, string SourceSha256, string ManifestSha256,
    string BlockId, string RawTextSha256, int PhysicalPage, string BlockType,
    string Engine, string EngineVersion, string Method, string StageId, int RawLineCount,
    CanonicalPolygon? ObservedPolygon, IReadOnlyList<string> QualitySignals);

internal sealed record AdvancedAnalysisNativeObservationInventory(
    string Availability, string? Reason, int? ObservationCount, int Offset,
    int? NextOffset, IReadOnlyList<AdvancedAnalysisNativeObservationDescriptor>? Observations);

internal sealed record AdvancedAnalysisNativeObservationLine(
    string Text, AdvancedAnalysisNativeObservationReference Source);

internal sealed record AdvancedAnalysisNativeObservationWindow(
    string BlockId, string RawTextSha256, int PhysicalPage, string OffsetUnit, int Offset,
    int Limit, int RawLineCount, int? NextOffset, string Status,
    IReadOnlyList<AdvancedAnalysisNativeObservationLine> Lines);

internal static class AdvancedAnalysisNativeObservationWindows
{
    private const int MaximumRawCharacters = 4_000_000;
    private const int MaximumRawLines = 50_000;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static AdvancedAnalysisNativeObservationInventory Inventory(CanonicalDocument document,
        int physicalPage, int offset, int limit, int maximumCharacters, CancellationToken ct = default)
        => Inventory(NativeObservationPageReadContext.FromInline(Guid.Empty, document, physicalPage), offset, limit, maximumCharacters, ct);

    internal static AdvancedAnalysisNativeObservationInventory Inventory(NativeObservationPageReadContext page,
        int offset, int limit, int maximumCharacters, CancellationToken ct = default)
    {
        ValidatePage(page);
        WindowArguments(offset, limit, maximumCharacters);
        var blocks = (page.Blocks ?? []).Where(block => CanonicalObservationSelector.IsNativeObservation(block.BlockType))
            .OrderBy(block => block.ReadingOrder).ThenBy(block => block.Ordinal)
            .ThenBy(block => block.BlockId, StringComparer.Ordinal).ToArray();
        // A missing retained observation says nothing about whether acquisition
        // ran, succeeded or observed zero source characters.
        if (blocks.Length == 0)
            return Fits(new("unavailable", "no_captured_native_observation", null, offset, null, null));
        if (offset > blocks.Length)
            throw Invalid();
        var selected = blocks.Skip(offset).Take(limit).Select(block =>
        {
            ct.ThrowIfCancellationRequested();
            var lines = Lines(page, block, ct);
            return new AdvancedAnalysisNativeObservationDescriptor(page.Identity.DocumentId.ToString("D"),
                page.Identity.RevisionId.ToString("D"), page.Identity.SourceSha256, page.Identity.ManifestSha256,
                block.BlockId, block.RawSha256, page.PhysicalPage, block.BlockType,
                block.Engine, block.EngineVersion, block.Method,
                block.StageId, lines.Count, block.ObservedPolygon,
                page.PageQualityFlags.Concat(block.QualityFlags).Distinct(StringComparer.Ordinal).ToArray());
        }).ToList();
        var result = Create(null);
        while (Size(result) > maximumCharacters && selected.Count > 0)
        {
            selected.RemoveAt(selected.Count - 1);
            result = Create(selected.Count == 0 ? "descriptor_does_not_fit" : "partial_budget");
        }
        return Fits(result);

        AdvancedAnalysisNativeObservationInventory Create(string? reason)
            => new("observed", reason, blocks.Length, offset,
                offset + selected.Count < blocks.Length ? offset + selected.Count : null, selected.ToArray());
        AdvancedAnalysisNativeObservationInventory Fits(AdvancedAnalysisNativeObservationInventory value)
            => Size(value) <= maximumCharacters ? value : throw BudgetInvalid();
    }

    internal static AdvancedAnalysisNativeObservationWindow Raw(CanonicalDocument document,
        int physicalPage, string blockId, string rawSha256, int offset, int limit, int maximumCharacters,
        CancellationToken ct = default)
        => Raw(NativeObservationPageReadContext.FromInline(Guid.Empty, document, physicalPage), blockId, rawSha256,
            offset, limit, maximumCharacters, ct);

    internal static AdvancedAnalysisNativeObservationWindow Raw(NativeObservationPageReadContext page,
        string blockId, string rawSha256, int offset, int limit, int maximumCharacters,
        CancellationToken ct = default)
    {
        ValidatePage(page);
        WindowArguments(offset, limit, maximumCharacters);
        var block = Block(page, blockId, rawSha256);
        var all = Lines(page, block, ct);
        if (offset > all.Count)
            throw Invalid();
        var selected = all.Skip(offset).Take(limit).ToList();
        var expected = selected.Count;
        var result = Create("observed");
        while (Size(result) > maximumCharacters && selected.Count > 0)
        {
            selected.RemoveAt(selected.Count - 1);
            result = Create(selected.Count == 0 && expected > 0 ? "line_does_not_fit" : "partial_budget");
        }
        if (Size(result) > maximumCharacters)
            throw BudgetInvalid();
        return result;

        AdvancedAnalysisNativeObservationWindow Create(string status)
            => new(block.BlockId, block.RawSha256, page.PhysicalPage, "raw_line_ordinal", offset,
                limit, all.Count, offset + selected.Count < all.Count ? offset + selected.Count : null,
                status, selected.ToArray());
    }

    internal static AdvancedAnalysisNativeObservationLine ResolveLine(CanonicalDocument document,
        AdvancedAnalysisNativeObservationReference reference, CancellationToken ct = default)
        => ResolveLine(NativeObservationPageReadContext.FromInline(Guid.Empty, document, reference.PhysicalPage), reference, ct);

    internal static AdvancedAnalysisNativeObservationLine ResolveLine(NativeObservationPageReadContext page,
        AdvancedAnalysisNativeObservationReference reference, CancellationToken ct = default)
    {
        ValidateReference(reference);
        ValidatePage(page);
        if (reference.PhysicalPage != page.PhysicalPage)
            throw Invalid();
        var block = Block(page, reference.BlockId, reference.RawTextSha256);
        var lines = Lines(page, block, ct);
        return ValidateLine(reference, lines);
    }

    internal static IReadOnlyList<AdvancedAnalysisNativeObservationLine> ObservedLines(CanonicalDocument document,
        int physicalPage, string blockId, string rawSha256, CancellationToken ct)
        => ObservedLines(NativeObservationPageReadContext.FromInline(Guid.Empty, document, physicalPage), blockId, rawSha256, ct);

    internal static IReadOnlyList<AdvancedAnalysisNativeObservationLine> ObservedLines(NativeObservationPageReadContext page,
        string blockId, string rawSha256, CancellationToken ct)
    {
        ValidatePage(page);
        return Lines(page, Block(page, blockId, rawSha256), ct);
    }

    internal static AdvancedAnalysisNativeObservationLine ValidateLine(AdvancedAnalysisNativeObservationReference reference,
        IReadOnlyList<AdvancedAnalysisNativeObservationLine> lines)
    {
        ValidateReference(reference);
        if (reference.LineOrdinal < 0 || reference.LineOrdinal >= lines.Count)
            throw Invalid();
        var line = lines[reference.LineOrdinal];
        if (reference.LineId != line.Source.LineId
            || !string.Equals(reference.LineTextSha256, line.Source.LineTextSha256, StringComparison.OrdinalIgnoreCase)
            || reference.UnicodeScalarStart != line.Source.UnicodeScalarStart
            || reference.UnicodeScalarEndExclusive != line.Source.UnicodeScalarEndExclusive)
            throw Invalid();
        return line;
    }

    internal static void ValidateReference(AdvancedAnalysisNativeObservationReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.BlockId) || reference.BlockId.Length > 200
            || reference.RawTextSha256 is not { Length: 64 } raw || !raw.All(char.IsAsciiHexDigit)
            || reference.LineTextSha256 is not { Length: 64 } text || !text.All(char.IsAsciiHexDigit)
            || reference.LineId is not { Length: 76 } line || !line.StartsWith("native_line_", StringComparison.Ordinal)
            || !line[12..].All(char.IsAsciiHexDigit)
            || reference.PhysicalPage <= 0 || reference.LineOrdinal < 0
            || reference.UnicodeScalarStart < 0 || reference.UnicodeScalarEndExclusive <= reference.UnicodeScalarStart)
            throw Invalid();
    }

    private static void ValidatePage(NativeObservationPageReadContext page)
    {
        NativeObservationPageReadContext.ValidateIdentity(page.Identity.DocumentId, page.Identity.RevisionId,
            page.Identity.SourceSha256, page.Identity.ManifestSha256);
        if (page.PhysicalPage <= 0 || page.PhysicalPage > page.PageCount)
            throw Invalid();
    }

    private static NativeRawBlockSnapshot Block(NativeObservationPageReadContext page, string blockId, string rawSha256)
    {
        var matches = (page.Blocks ?? []).Where(block => block.BlockId == blockId
            && CanonicalObservationSelector.IsNativeObservation(block.BlockType)).ToArray();
        if (matches.Length != 1 || !string.Equals(matches[0].RawSha256, rawSha256, StringComparison.OrdinalIgnoreCase))
            throw Invalid();
        return matches[0];
    }

    private static IReadOnlyList<AdvancedAnalysisNativeObservationLine> Lines(NativeObservationPageReadContext page,
        NativeRawBlockSnapshot block, CancellationToken ct)
    {
        var raw = block.Raw;
        if (raw.Length > MaximumRawCharacters)
            throw new AdvancedAnalysisToolException("native_observation_source_budget_exceeded");
        if (!string.Equals(Hash(raw), block.RawSha256, StringComparison.OrdinalIgnoreCase))
            throw Invalid();
        var result = new List<AdvancedAnalysisNativeObservationLine>();
        var characterStart = 0;
        var scalarStart = 0;
        var scalarCursor = 0;
        var cursor = 0;
        while (cursor < raw.Length)
        {
            if ((scalarCursor & 1023) == 0)
                ct.ThrowIfCancellationRequested();
            if (!Rune.TryGetRuneAt(raw, cursor, out var rune))
                throw new AdvancedAnalysisToolException("native_observation_unicode_invalid");
            cursor += rune.Utf16SequenceLength;
            scalarCursor++;
            if (rune.Value is not '\r' and not '\n')
                continue;
            if (rune.Value == '\r' && cursor < raw.Length && raw[cursor] == '\n')
            {
                cursor++;
                scalarCursor++;
            }
            AddLine();
        }
        if (characterStart < raw.Length)
            AddLine();
        ct.ThrowIfCancellationRequested();
        return result;

        void AddLine()
        {
            if (result.Count >= MaximumRawLines)
                throw new AdvancedAnalysisToolException("native_observation_source_budget_exceeded");
            var text = raw[characterStart..cursor];
            var textSha = Hash(text);
            var ordinal = result.Count;
            var identity = JsonSerializer.Serialize(new object[] { page.Identity.DocumentId, page.Identity.RevisionId,
                page.Identity.SourceSha256.ToLowerInvariant(), page.Identity.ManifestSha256.ToLowerInvariant(), page.PhysicalPage,
                block.BlockId, block.RawSha256.ToLowerInvariant(), ordinal, scalarStart, scalarCursor, textSha });
            result.Add(new(text, new()
            {
                BlockId = block.BlockId, RawTextSha256 = block.RawSha256, PhysicalPage = page.PhysicalPage,
                LineId = "native_line_" + Hash(identity), LineOrdinal = ordinal, LineTextSha256 = textSha,
                UnicodeScalarStart = scalarStart, UnicodeScalarEndExclusive = scalarCursor
            }));
            characterStart = cursor;
            scalarStart = scalarCursor;
        }
    }

    private static void WindowArguments(int offset, int limit, int maximumCharacters)
    {
        if (offset < 0 || limit is < 1 or > 64 || maximumCharacters < 256)
            throw Invalid();
    }

    private static int Size<T>(T value) => JsonSerializer.Serialize(value, Web).Length;
    private static string Hash(string text)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text))).ToLowerInvariant();
        }
        catch (EncoderFallbackException)
        {
            throw new AdvancedAnalysisToolException("native_observation_unicode_invalid");
        }
    }
    private static AdvancedAnalysisToolException Invalid() => new("native_observation_identity_or_window_invalid");
    private static AdvancedAnalysisToolException BudgetInvalid() => new("native_observation_budget_diagnostic_does_not_fit");
}
