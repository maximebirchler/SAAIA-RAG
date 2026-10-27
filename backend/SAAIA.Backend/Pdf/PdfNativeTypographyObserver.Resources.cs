using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

internal static partial class PdfNativeTypographyObserver
{
    private static List<CanonicalNativeFontResource> ObserveResources(Page page, Structure? structure, string source,
        PdfNativeTypographyBudget budget, Action check)
    {
        var result = new List<CanonicalNativeFontResource>(); var references = 1024;
        var seen = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance);
        var parents = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance);
        var current = page.Dictionary;
        for (var depth = 0; depth < 32; depth++)
        {
            check(); if (!parents.Add(current)) throw new Refusal("native_typography_resource_cycle");
            if (current.TryGet(NameToken.Resources, out var resourceToken) && Resolve(resourceToken) is not NullToken)
            {
                Visit(Resolve(resourceToken) as DictionaryToken ?? throw new Refusal("native_typography_resources_invalid"), "page.parent[" + depth + "]", 0);
                return result;
            }
            if (!current.TryGet(NameToken.Parent, out var parent) || Resolve(parent) is NullToken) return result;
            current = Resolve(parent) as DictionaryToken ?? throw new Refusal("native_typography_resources_invalid");
        }
        throw new Refusal("native_typography_parent_limit");

        void Visit(DictionaryToken resources, string scope, int depth)
        {
            check(); if (depth > 16 || scope.Length > 512) throw new Refusal("native_typography_form_depth_limit");
            if (!seen.Add(resources)) return;
            if (seen.Count > 256) throw new Refusal("native_typography_resource_dictionary_limit");
            if (resources.TryGet(NameToken.Font, out var fonts))
            {
                var dictionary = Resolve(fonts) as DictionaryToken ?? throw new Refusal("native_typography_font_dictionary_invalid");
                foreach (var pair in dictionary.Data)
                {
                    check();
                    if (result.Count >= CanonicalNativeTypographyValidator.MaximumResourcesPerPage || budget.RemainingResources <= 0)
                        throw new Refusal("native_typography_font_resource_limit");
                    budget.RemainingResources--;
                    var font = Resolve(pair.Value) as DictionaryToken ?? throw new Refusal("native_typography_font_dictionary_invalid");
                    var item = new CanonicalNativeFontResource
                    {
                        ResourceScopePath = scope, ResourceName = "/" + pair.Key, Subtype = Name(font, NameToken.Subtype), BaseFont = Name(font, NameToken.BaseFont)
                    };
                    if (item.ResourceName.Length > 512) throw new Refusal("native_typography_font_name_limit");
                    if (pair.Value is IndirectReferenceToken fontRef)
                    { item.FontObjectNumber = checked((int)fontRef.Data.ObjectNumber); item.FontObjectGeneration = fontRef.Data.Generation; }
                    DictionaryToken? descriptor = null;
                    if (item.Subtype == "Type0")
                    {
                        // Composite descendants are outside this bounded observer's acquisition scope.
                        // A missing direct descriptor is not evidence that their descriptor/program is absent.
                        item.DescriptorAvailability = "unavailable";
                        item.DescriptorReason = "native_typography_composite_descendant_not_observed";
                    }
                    else if (font.TryGet(NameToken.FontDescriptor, out var descriptorToken) && Resolve(descriptorToken) is not NullToken)
                    {
                        descriptor = Resolve(descriptorToken) as DictionaryToken;
                        item.DescriptorAvailability = descriptor is null ? "unavailable" : "observed";
                        if (descriptor is null) item.DescriptorReason = "native_typography_direct_descriptor_invalid";
                        if (descriptorToken is IndirectReferenceToken descriptorRef)
                        { item.DescriptorObjectNumber = checked((int)descriptorRef.Data.ObjectNumber); item.DescriptorObjectGeneration = descriptorRef.Data.Generation; }
                    }
                    item.DescriptorName = TextField(descriptor, "FontName", item);
                    item.FontFamily = TextField(descriptor, "FontFamily", item);
                    var flags = NumberField(descriptor, "Flags", item);
                    if (flags is not null && (flags != Math.Truncate(flags.Value) || flags < int.MinValue || flags > int.MaxValue))
                    { item.FieldAvailability["Flags"] = "invalid_type"; flags = null; }
                    item.Flags = flags is null ? null : (int)flags.Value;
                    item.ItalicAngle = NumberField(descriptor, "ItalicAngle", item);
                    item.FontWeight = NumberField(descriptor, "FontWeight", item);
                    item.FontProgramDeclaration = descriptor is null ? item.DescriptorAvailability == "absent" ? "absent" : "unavailable"
                        : new[] { NameToken.FontFile, NameToken.FontFile2, NameToken.FontFile3 }
                            .Any(key => descriptor.TryGet(key, out var value) && Resolve(value) is not NullToken) ? "declared" : "absent";
                    item.ResourceSnapshotSha256 = CanonicalNativeTypographyIdentity.ResourceSnapshot(item);
                    item.ResourceId = CanonicalNativeTypographyIdentity.ResourceId(source, page.Number, item); result.Add(item);
                }
            }
            if (!resources.TryGet(NameToken.Xobject, out var xobjectsToken)) return;
            var xobjects = Resolve(xobjectsToken) as DictionaryToken ?? throw new Refusal("native_typography_xobjects_invalid");
            foreach (var pair in xobjects.Data)
            {
                check(); if (Resolve(pair.Value) is not StreamToken stream) throw new Refusal("native_typography_xobject_invalid");
                if (Name(stream.StreamDictionary, NameToken.Subtype) != "Form") continue;
                if (stream.StreamDictionary.TryGet(NameToken.Resources, out var formResources))
                    Visit(Resolve(formResources) as DictionaryToken ?? throw new Refusal("native_typography_form_resources_invalid"), scope + "/form[" + pair.Key + "]", depth + 1);
            }
        }
        string? Name(DictionaryToken dictionary, NameToken key)
        {
            if (!dictionary.TryGet(key, out var token) || Resolve(token) is not NameToken name) return null;
            if (name.Data.Length > 512) throw new Refusal("native_typography_font_name_limit");
            return name.Data;
        }
        string? TextField(DictionaryToken? descriptor, string key, CanonicalNativeFontResource item)
        {
            if (descriptor is null) { item.FieldAvailability[key] = item.DescriptorAvailability == "absent" ? "absent" : "unavailable"; return null; }
            if (!descriptor.TryGet(NameToken.Create(key), out var value)) { item.FieldAvailability[key] = "absent"; return null; }
            var resolved = Resolve(value); var text = resolved is NameToken name ? name.Data : resolved is StringToken str ? str.Data : null;
            item.FieldAvailability[key] = text is null ? "invalid_type" : "observed";
            if (text?.Length > 512) throw new Refusal("native_typography_font_name_limit");
            return text;
        }
        double? NumberField(DictionaryToken? descriptor, string key, CanonicalNativeFontResource item)
        {
            if (descriptor is null) { item.FieldAvailability[key] = item.DescriptorAvailability == "absent" ? "absent" : "unavailable"; return null; }
            if (!descriptor.TryGet(NameToken.Create(key), out var value)) { item.FieldAvailability[key] = "absent"; return null; }
            var numeric = Resolve(value) as NumericToken;
            item.FieldAvailability[key] = numeric is null || !double.IsFinite(numeric.Double) ? "invalid_type" : "observed";
            return numeric is not null && double.IsFinite(numeric.Double) ? numeric.Double : null;
        }
        IToken Resolve(IToken token)
        {
            var chain = new HashSet<IndirectReference>();
            while (token is IndirectReferenceToken reference)
            {
                check(); if (structure is null || references-- <= 0 || !chain.Add(reference.Data)) throw new Refusal("native_typography_reference_limit");
                token = structure.GetObject(reference.Data).Data;
            }
            return token;
        }
    }
}
