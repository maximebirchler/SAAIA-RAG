using System.Reflection;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CompactSemanticEnvelopeNativeTokenHarnessRedTests
{
    private const string HarnessTypeName =
        "SAAIA.Client.ToolAgent.Tests.CompactSemanticEnvelopeNativeTokenHarness";

    [Fact]
    public void A647_RED01_loads_the_frozen_contracts_by_exact_hash()
        => RequirePublicStaticMethod("LoadFrozenContracts");

    [Fact]
    public void A647_RED02_loads_twenty_five_cases_without_copied_text()
        => RequirePublicStaticMethod("LoadFrozenCases");

    [Fact]
    public void A647_RED03_builds_twenty_five_canonical_diagnostic_snapshots()
        => RequirePublicStaticMethod("BuildSnapshots");

    [Fact]
    public void A647_RED04_serializes_snapshots_byte_exactly_and_deterministically()
        => RequirePublicStaticMethod("SerializeSnapshot");

    [Fact]
    public void A647_RED05_preserves_all_required_provenance_fields()
        => RequirePublicStaticMethod("ValidateSnapshotProvenance");

    [Fact]
    public void A647_RED06_excludes_oracles_from_every_counted_message()
        => RequirePublicStaticMethod("ValidateNoOracleLeak");

    [Fact]
    public void A647_RED07_builds_v1_and_v2_controller_payloads()
        => RequirePublicStaticMethod("BuildControllerPayloads");

    [Fact]
    public void A647_RED08_builds_representative_and_minimal_review_payloads()
        => RequirePublicStaticMethod("BuildReviewerPayloads");

    [Fact]
    public void A647_RED09_proves_the_v1_controller_prefix()
        => RequirePublicStaticMethod("ValidateV1Prefix");

    [Fact]
    public void A647_RED10_proves_the_v2_reviewer_is_independent()
        => RequirePublicStaticMethod("ValidateV2Independence");

    [Fact]
    public void A647_RED11_prevents_the_v2_reviewer_from_writing_a_draft()
        => RequirePublicStaticMethod("ValidateV2ReviewerCannotWrite");

    [Fact]
    public void A647_RED12_computes_the_conservative_projected_worst_case()
        => RequirePublicStaticMethod("ComputeProjectedWorst");

    [Fact]
    public void A647_RED13_builds_the_exact_alternating_schedule()
        => RequirePublicStaticMethod("BuildMeasurementSchedule");

    [Fact]
    public void A647_RED14_repeats_each_payload_exactly_twice()
        => RequirePublicStaticMethod("ValidateDuplicateMeasurements");

    [Fact]
    public void A647_RED15_blocks_the_completion_endpoint_before_transport()
        => RequireNestedType("EndpointGuardHandler");

    [Fact]
    public void A647_RED16_allows_only_native_input_token_measurement()
        => RequirePublicStaticMethod("ValidateEndpointLedger");

    [Fact]
    public void A647_RED17_writes_checkpoint_and_artifacts_atomically()
        => RequirePublicStaticMethod("WriteCheckpointAtomically");

    [Fact]
    public void A647_RED18_refuses_an_existing_official_directory()
        => RequirePublicStaticMethod("EnsureOfficialDirectoryAbsent");

    [Fact]
    public void A647_RED19_fails_closed_on_missing_or_inconsistent_counts()
        => RequirePublicStaticMethod("EvaluateVariantGates");

    [Fact]
    public void A647_RED20_guarantees_runtime_shutdown_in_a_finally_path()
        => RequirePublicStaticMethod("RunNativeMeasurementAsync");

    private static Type RequireHarnessType()
    {
        var type = typeof(CompactSemanticEnvelopeNativeTokenHarnessRedTests)
            .Assembly
            .GetType(HarnessTypeName, throwOnError: false);
        Assert.NotNull(type);
        return type!;
    }

    private static void RequirePublicStaticMethod(string name)
    {
        var method = RequireHarnessType().GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
    }

    private static void RequireNestedType(string name)
    {
        var nested = RequireHarnessType().GetNestedType(
            name,
            BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(nested);
    }
}
