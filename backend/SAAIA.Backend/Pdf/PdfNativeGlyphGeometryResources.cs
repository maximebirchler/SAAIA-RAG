using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

internal sealed record PdfNativeGlyphFontResource(string BaseFont, string Subtype, string? DescriptorName,
    bool HasDescriptor, bool HasTypedSimpleMetrics, string Embedding, IReadOnlyList<string> Names);
internal sealed record PdfNativeGlyphResourceInventory(IReadOnlyList<PdfNativeGlyphFontResource> Fonts, string? Reason);

/// <summary>Bounded raw-resource inventory including inherited resources and Form XObjects.</summary>
internal static class PdfNativeGlyphGeometryResources
{
    private const int MaximumParents = 32;
    private const int MaximumFormDepth = 16;
    private const int MaximumReferences = 1024;
    private const int MaximumFonts = 256;
    private const int MaximumResourceDictionaries = 256;

    internal static PdfNativeGlyphResourceInventory Observe(Page page, Structure? structure, CancellationToken ct)
    {
        var remaining = MaximumReferences;
        var fonts = new List<PdfNativeGlyphFontResource>();
        var fontDictionaries = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance);
        var fontReferences = new HashSet<IndirectReference>();
        var resourceDictionaries = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance);
        try
        {
            var parents = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance);
            var current = page.Dictionary; DictionaryToken? resources = null; var complete = false;
            for (var depth = 0; depth < MaximumParents; depth++)
            {
                ct.ThrowIfCancellationRequested();
                if (!parents.Add(current)) throw new InvalidDataException("native_glyph_resource_cycle");
                if (current.TryGet(NameToken.Resources, out var token) && Resolve(token) is not NullToken)
                { resources = Resolve(token) as DictionaryToken ?? throw new InvalidDataException("native_glyph_resources_invalid"); complete = true; break; }
                if (!current.TryGet(NameToken.Parent, out var parent) || Resolve(parent) is NullToken) { complete = true; break; }
                current = Resolve(parent) as DictionaryToken ?? throw new InvalidDataException("native_glyph_resources_invalid");
            }
            if (!complete) throw new InvalidDataException("native_glyph_parent_limit");
            if (resources is null) return new([], "native_glyph_resources_unavailable");
            Visit(resources, 0);
            return new(fonts, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException error) when (error.Message.StartsWith("native_glyph_", StringComparison.Ordinal))
        { return new(fonts, error.Message); }
        catch { return new(fonts, "native_glyph_resources_unavailable"); }

        void Visit(DictionaryToken resources, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > MaximumFormDepth) throw new InvalidDataException("native_glyph_form_depth_limit");
            if (!resourceDictionaries.Add(resources)) return;
            if (resourceDictionaries.Count > MaximumResourceDictionaries) throw new InvalidDataException("native_glyph_resource_limit");
            if (resources.TryGet(NameToken.Font, out var fontToken))
            {
                var dictionary = Resolve(fontToken) as DictionaryToken ?? throw new InvalidDataException("native_glyph_font_resources_invalid");
                foreach (var pair in dictionary.Data)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pair.Value is IndirectReferenceToken fontReference && !fontReferences.Add(fontReference.Data)) continue;
                    var font = Resolve(pair.Value) as DictionaryToken ?? throw new InvalidDataException("native_glyph_font_resource_invalid");
                    if (!fontDictionaries.Add(font)) continue;
                    if (fontDictionaries.Count > MaximumFonts) throw new InvalidDataException("native_glyph_font_resource_limit");
                    var baseName = Name(font, NameToken.BaseFont); var subtype = Name(font, NameToken.Subtype);
                    DictionaryToken? descriptor = null;
                    if (font.TryGet(NameToken.FontDescriptor, out var descriptorToken) && Resolve(descriptorToken) is not NullToken)
                        descriptor = Resolve(descriptorToken) as DictionaryToken ?? throw new InvalidDataException("native_glyph_font_descriptor_invalid");
                    var descriptorName = descriptor is null ? null : Name(descriptor, NameToken.FontName);
                    var embedded = descriptor is not null && new[] { NameToken.FontFile, NameToken.FontFile2, NameToken.FontFile3 }
                        .Any(key => descriptor.TryGet(key, out var file) && Resolve(file) is not NullToken);
                    var names = new[] { baseName, descriptorName }.Where(value => !string.IsNullOrEmpty(value)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
                    fonts.Add(new(baseName ?? "", subtype ?? "unknown", descriptorName, descriptor is not null,
                        HasTypedSimpleMetrics(font), embedded ? "declared" : "absent", names));
                }
            }
            if (!resources.TryGet(NameToken.Xobject, out var xobjectsToken)) return;
            var xobjects = Resolve(xobjectsToken) as DictionaryToken ?? throw new InvalidDataException("native_glyph_xobject_resources_invalid");
            foreach (var pair in xobjects.Data)
            {
                ct.ThrowIfCancellationRequested();
                if (Resolve(pair.Value) is not StreamToken stream) throw new InvalidDataException("native_glyph_xobject_invalid");
                if (Name(stream.StreamDictionary, NameToken.Subtype) != "Form") continue;
                if (stream.StreamDictionary.TryGet(NameToken.Resources, out var formResources))
                    Visit(Resolve(formResources) as DictionaryToken ?? throw new InvalidDataException("native_glyph_form_resources_invalid"), depth + 1);
                // A form without its own resources uses the already inventoried caller scope.
            }
        }
        string? Name(DictionaryToken dictionary, NameToken key)
            => dictionary.TryGet(key, out var value) && Resolve(value) is NameToken name ? name.Data : null;
        bool HasTypedSimpleMetrics(DictionaryToken font)
        {
            // Without these entries PdfPig may choose its separate Standard14
            // fallback branch. Do not claim the descriptor-bbox branch from the
            // subtype alone. Simple-font character codes are bounded to 0..255.
            return font.TryGet(NameToken.FirstChar, out var firstToken) && Resolve(firstToken) is NumericToken first
                && double.IsFinite(first.Double) && first.Double == Math.Floor(first.Double) && first.Double is >= 0 and <= 255
                && font.TryGet(NameToken.Widths, out var widthsToken) && Resolve(widthsToken) is ArrayToken widths
                && widths.Length is > 0 and <= 256 && first.Double + widths.Length <= 256
                && widths.Data.All(value => Resolve(value) is NumericToken width && double.IsFinite(width.Double));
        }
        IToken Resolve(IToken value)
        {
            var seen = new HashSet<IndirectReference>();
            while (value is IndirectReferenceToken reference)
            {
                ct.ThrowIfCancellationRequested();
                if (structure is null || remaining-- <= 0 || !seen.Add(reference.Data)) throw new InvalidDataException("native_glyph_indirect_reference_limit");
                value = structure.GetObject(reference.Data).Data;
            }
            return value;
        }
    }
}
