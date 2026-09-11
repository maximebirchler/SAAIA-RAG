namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static EvidenceBundle ApplyNamedDocumentIdentityContract(
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        out IReadOnlyList<string> mismatchedEvidenceIds)
    {
        if (!SourceBackedNamedDocumentIdentity.TryGetResolvedRequestedIdentity(
                intake,
                out var expected))
        {
            mismatchedEvidenceIds = Array.Empty<string>();
            return bundle;
        }

        var mismatches = new List<string>();
        var items = bundle.Items.Select(item =>
        {
            if (SourceBackedNamedDocumentIdentity.Matches(expected, item))
                return item;
            mismatches.Add(item.EvidenceId);
            return item with
            {
                RiskFlags = item.RiskFlags
                    .Append(SourceBackedNamedDocumentIdentity.MismatchRiskFlag)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }).ToArray();
        mismatchedEvidenceIds = mismatches;
        return mismatches.Count == 0 ? bundle : bundle with { Items = items };
    }

    private static bool IsRequestedDocumentIdentityEligible(EvidenceItem item)
        => !item.RiskFlags.Contains(
            SourceBackedNamedDocumentIdentity.MismatchRiskFlag,
            StringComparer.OrdinalIgnoreCase);

#if DEBUG || SAAIA_TEST_HOOKS
    internal static EvidenceBundle ApplyNamedDocumentIdentityContractForTests(
        SourceBackedIntake intake,
        EvidenceBundle bundle)
        => ApplyNamedDocumentIdentityContract(
            intake,
            bundle,
            out _);
#endif
}
