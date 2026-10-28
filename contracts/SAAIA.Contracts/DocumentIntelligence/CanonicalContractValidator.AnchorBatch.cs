namespace SAAIA.Contracts.DocumentIntelligence;

public static partial class CanonicalContractValidator
{
    // Standalone safe entry point: never accepts a reusable "already validated"
    // token or a caller-supplied iterator executed after document validation.
    public static void ValidateAnchorsOrThrow(
        CanonicalDocument document,
        IReadOnlyList<SourceAnchor> anchors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(anchors);
        var materialized = anchors.ToArray();
        ValidateOrThrow(document);
        var index = new CanonicalAnchorValidationIndex(document);
        foreach (var anchor in materialized)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            ThrowIfAny(ValidateAnchorForValidatedDocument(anchor, document, index));
        }
    }
}
