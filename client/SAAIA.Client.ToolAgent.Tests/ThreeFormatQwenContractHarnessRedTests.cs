using System.Reflection;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ThreeFormatQwenContractHarnessRedTests
{
    private const string HarnessTypeName =
        "SAAIA.Client.ToolAgent.Tests.ThreeFormatQwenContractHarness";

    [Fact]
    public void A657_RED01_loads_the_three_frozen_contract_families_by_hash()
        => RequirePublicStaticMethod("LoadFrozenInputs");

    [Fact]
    public void A657_RED02_builds_a_direct_native_palette_request()
        => RequirePublicStaticMethod("BuildDirectRequest");

    [Fact]
    public void A657_RED03_builds_route_then_specialized_payload_requests()
    {
        RequirePublicStaticMethod("BuildRouteRequest");
        RequirePublicStaticMethod("BuildSpecializedPayloadRequest");
    }

    [Fact]
    public void A657_RED04_builds_a_strict_grammar_json_request()
        => RequirePublicStaticMethod("BuildGrammarRequest");

    [Fact]
    public void A657_RED05_parses_and_validates_direct_tool_calls()
        => RequirePublicStaticMethod("ParseDirectResponse");

    [Fact]
    public void A657_RED06_parses_route_then_exact_specialized_payload()
    {
        RequirePublicStaticMethod("ParseRouteResponse");
        RequirePublicStaticMethod("ParseSpecializedPayloadResponse");
    }

    [Fact]
    public void A657_RED07_parses_strict_grammar_json_without_fallback()
        => RequirePublicStaticMethod("ParseGrammarResponse");

    [Fact]
    public void A657_RED08_has_a_frozen_fixture_executor_for_all_real_routes()
        => RequireNestedType("FrozenFixtureExecutor");

    [Fact]
    public void A657_RED09_writes_atomic_checkpoints_and_append_only_jsonl()
    {
        RequirePublicStaticMethod("WriteCheckpointAtomically");
        RequirePublicStaticMethod("AppendJsonLine");
    }

    [Fact]
    public void A657_RED10_builds_deterministic_blind_packets()
        => RequirePublicStaticMethod("BuildBlindPacket");

    [Fact]
    public void A657_RED11_runs_the_frozen_order_with_fail_fast()
        => RequirePublicStaticMethod("RunFailFast");

    private static void RequirePublicStaticMethod(string name)
    {
        var type = typeof(ThreeFormatQwenContractHarnessRedTests).Assembly
            .GetType(HarnessTypeName, throwOnError: false);
        Assert.NotNull(type);
        Assert.NotNull(type!.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Static));
    }

    private static void RequireNestedType(string name)
    {
        var type = typeof(ThreeFormatQwenContractHarnessRedTests).Assembly
            .GetType(HarnessTypeName, throwOnError: false);
        Assert.NotNull(type);
        Assert.NotNull(type!.GetNestedType(
            name,
            BindingFlags.Public));
    }
}
