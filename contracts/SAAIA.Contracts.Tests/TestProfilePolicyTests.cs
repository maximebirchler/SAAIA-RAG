using Xunit;

namespace SAAIA.Contracts.Tests;

public sealed class TestProfilePolicyTests
{
    [Theory]
    [InlineData("IntegrationPg")]
    [InlineData("LiveLocal")]
    [InlineData("LivePaid")]
    public void Unit_discovery_skips_every_integration_or_live_profile(string profile)
        => Assert.NotNull(TestProfilePolicy.SkipReason(profile, "Unit"));

    [Fact]
    public void Explicit_matching_profile_is_not_silently_skipped()
        => Assert.Null(TestProfilePolicy.SkipReason("IntegrationPg", "IntegrationPg"));

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("1", "0", "1")]
    [InlineData("1", "1", "0")]
    [InlineData("1", "1", "NaN")]
    public void Paid_discovery_rejects_incomplete_authorization(string? paid, string? transmission, string? cap)
        => Assert.Throws<InvalidOperationException>(() => TestProfilePolicy.ValidatePaidAuthorization(paid, transmission, cap));

    [Fact]
    public void Complete_paid_authorization_can_be_validated_without_a_provider_call()
        => TestProfilePolicy.ValidatePaidAuthorization("1", "1", "0.25");
}
