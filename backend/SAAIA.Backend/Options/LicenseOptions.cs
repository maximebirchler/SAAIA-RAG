namespace SAAIA.Backend;

sealed class LicenseOptions
{
    public int Seats { get; set; } = 1;

    public bool AdvancedAnalysisEnabled { get; set; }
}
