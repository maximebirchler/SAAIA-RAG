using Xunit;

namespace SAAIA.Backend.Tests;

/// <summary>An unavailable OCR runtime is an explicit skip, never a successful execution.</summary>
public sealed class OcrEngineFactAttribute : FactAttribute
{
    public OcrEngineFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SAAIA_TEST_OCR_E2E"), "1", StringComparison.Ordinal))
            Skip = "OCR engines not executed: set SAAIA_TEST_OCR_E2E=1 with explicitly provisioned local tools.";
    }
}
