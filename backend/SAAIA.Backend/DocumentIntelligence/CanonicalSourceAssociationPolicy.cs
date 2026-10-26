using SAAIA.Contracts.DocumentIntelligence;

/// <summary>All projections share the same boundary between observed text and asserted section links.</summary>
internal static class CanonicalSourceAssociationPolicy
{
    internal const string LayoutAssociationUncertainFlag = "canonical_layout_section_association_not_asserted";

    internal static bool IsUnassignedPhysicalContext(CanonicalBlock block)
        => block.IsRepeatedFurniture
           || block.QualityFlags.Contains(CanonicalRepeatedPageFurnitureClassifier.InferredQualityFlag, StringComparer.Ordinal)
           || block.Provenance.Attributes.TryGetValue("sectionAssociation", out var association)
               && string.Equals(association, "not_asserted", StringComparison.Ordinal);

    internal static void MarkLayoutPageAssociationsUncertain(CanonicalDocument document, CanonicalPage page)
    {
        // Geometry demonstrates a competing order, not a replacement subject
        // hierarchy. Keep every source observation while retracting only the
        // section relationships inferred from this page's disputed order.
        var affectedIds = page.Blocks
            .Where(block => block.BlockType != CanonicalNativeLayoutReconciler.RecoveryBlockType)
            .Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);
        foreach (var block in page.Blocks.Where(block => affectedIds.Contains(block.BlockId)))
        {
            block.Provenance.Attributes["sectionAssociation"] = "not_asserted";
            block.Provenance.Attributes["contextScope"] = "physical_page";
            block.Provenance.Attributes["sectionAssociationReason"] = "observed_native_layout_order_disagreement";
            if (!block.QualityFlags.Contains(LayoutAssociationUncertainFlag, StringComparer.Ordinal))
                block.QualityFlags.Add(LayoutAssociationUncertainFlag);
        }
        foreach (var section in document.SectionTree)
        {
            section.ContentBlockIds.RemoveAll(affectedIds.Contains);
            if (section.TitleBlockIds.Any(affectedIds.Contains))
                section.ParentSectionId = null;
        }
        if (!page.QualityFlags.Contains(LayoutAssociationUncertainFlag, StringComparer.Ordinal))
            page.QualityFlags.Add(LayoutAssociationUncertainFlag);
    }
}
