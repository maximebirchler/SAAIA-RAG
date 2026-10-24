global using SAAIA.Testing;

using Xunit;
using System.Reflection;

[assembly: SAAIA.Testing.TestProfileAuthorization]

namespace SAAIA.Testing;

// Discovery-time skips work with xUnit v2 and are represented as NotExecuted in TRX.
// A requested profile never skips a missing prerequisite: its test fails before work.
public sealed class ProfileFactAttribute : FactAttribute
{
    public ProfileFactAttribute(string profile) => Skip = TestProfilePolicy.SkipReason(profile);
}

public sealed class ProfileTheoryAttribute : TheoryAttribute
{
    public ProfileTheoryAttribute(string profile) => Skip = TestProfilePolicy.SkipReason(profile);
}

public static class TestProfilePolicy
{
    public static string? SkipReason(string profile)
    {
        var selected = Environment.GetEnvironmentVariable("SAAIA_TEST_PROFILE") ?? "Unit";
        return SkipReason(profile, selected);
    }

    public static string? SkipReason(string profile, string selected)
        => string.Equals(profile, selected, StringComparison.Ordinal)
            ? null : $"Requires explicit test profile {profile}; current profile is {selected}.";

    public static void ValidatePaidAuthorization(string? authorized, string? transmission, string? cap)
    {
        if (authorized != "1" || transmission != "1"
            || !decimal.TryParse(cap, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            throw new InvalidOperationException("LivePaid requires explicit paid and corpus-transmission authorization and a positive USD cap. Use the guarded campaign runner.");
    }
}

// Missing authorization must fail execution, not throw during discovery (some
// adapters turn discovery exceptions into an empty, successful-looking run).
public sealed class TestProfileAuthorizationAttribute : Xunit.Sdk.BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest)
    {
        if (Environment.GetEnvironmentVariable("SAAIA_TEST_PROFILE") != "LivePaid") return;
        TestProfilePolicy.ValidatePaidAuthorization(
            Environment.GetEnvironmentVariable("SAAIA_TEST_PAID_AUTHORIZED"),
            Environment.GetEnvironmentVariable("SAAIA_TEST_TRANSMISSION_AUTHORIZED"),
            Environment.GetEnvironmentVariable("SAAIA_TEST_PAID_CAP_USD"));
    }
}
