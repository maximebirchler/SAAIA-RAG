using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

internal sealed record PdfNativeCoordinateFrame(string Availability, int? RotationDegrees = null,
    double? CropBoxLeft = null, double? CropBoxBottom = null,
    double? MediaBoxLeft = null, double? MediaBoxBottom = null)
{
    private const int MaximumParentDepth = 32;
    private const int MaximumIndirectReferences = 64;

    internal static PdfNativeCoordinateFrame Observe(Page page, Structure? structure, CancellationToken ct)
    {
        int? rotation = null;
        try
        {
            rotation = page.Rotation.Value;
            var remainingReferences = MaximumIndirectReferences;
            var parents = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance);
            var parentReferences = new HashSet<IndirectReference>();
            var current = page.Dictionary;
            IToken? crop = null;
            IToken? media = null;
            var ancestryComplete = false;
            for (var depth = 0; depth < MaximumParentDepth; depth++)
            {
                ct.ThrowIfCancellationRequested();
                if (!parents.Add(current)) return new("unavailable", rotation);
                if (crop is null && current.TryGet(NameToken.CropBox, out var cropToken))
                {
                    var resolved = Resolve(cropToken);
                    if (resolved is not NullToken) crop = resolved;
                }
                if (media is null && current.TryGet(NameToken.MediaBox, out var mediaToken))
                {
                    var resolved = Resolve(mediaToken);
                    if (resolved is not NullToken) media = resolved;
                }
                if (crop is not null && media is not null) break;
                if (!current.TryGet(NameToken.Parent, out var parentToken) || parentToken is NullToken)
                {
                    ancestryComplete = true;
                    break;
                }
                if (parentToken is IndirectReferenceToken parentReference && !parentReferences.Add(parentReference.Data))
                    return new("unavailable", rotation);
                if (Resolve(parentToken) is not DictionaryToken parent) return new("unavailable", rotation);
                current = parent;
            }

            // CropBox defaults to MediaBox only after absence in the complete
            // page ancestry is proven. Page.CropBox is already normalized by
            // PdfPig and cannot establish the original PDF coordinate origin.
            if (media is null || crop is null && !ancestryComplete) return new("unavailable", rotation);
            if (!TryBox(media, out var mediaLeft, out var mediaBottom)) return new("unavailable", rotation);
            if (!TryBox(crop ?? media, out var cropLeft, out var cropBottom)) return new("unavailable", rotation);
            return new("available", rotation, cropLeft, cropBottom, mediaLeft, mediaBottom);

            IToken Resolve(IToken token)
            {
                var references = new HashSet<IndirectReference>();
                while (token is IndirectReferenceToken reference)
                {
                    ct.ThrowIfCancellationRequested();
                    if (structure is null || remainingReferences-- <= 0 || !references.Add(reference.Data))
                        throw new InvalidDataException("native_coordinate_frame_unavailable");
                    token = structure.GetObject(reference.Data).Data;
                }
                return token;
            }

            bool TryBox(IToken token, out double left, out double bottom)
            {
                left = bottom = 0;
                if (token is not ArrayToken { Length: 4 } array) return false;
                var coordinates = new double[4];
                for (var index = 0; index < coordinates.Length; index++)
                {
                    if (Resolve(array.Data[index]) is not NumericToken numeric || !double.IsFinite(numeric.Double))
                        return false;
                    coordinates[index] = numeric.Double;
                }
                if (coordinates[0] >= coordinates[2] || coordinates[1] >= coordinates[3]) return false;
                left = coordinates[0];
                bottom = coordinates[1];
                return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Do not guess an origin or expose parser exceptions/source data.
            return new("unavailable", rotation);
        }
    }
}
