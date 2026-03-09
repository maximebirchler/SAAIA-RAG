namespace SAAIA.Backend.CatalogSnapshot;

public sealed class CatalogSnapshotOptions
{
    /// <summary>
    /// Enable the periodic catalog snapshot builder.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval (seconds) between snapshot rebuilds.
    /// Default: 300 (5 minutes).
    /// </summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Startup delay before first run.
    /// Default: 10 seconds.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 10;

    /// <summary>
    /// Limit the number of documents read per tenant per run (safety valve).
    /// </summary>
    public int MaxDocsPerTenant { get; set; } = 500_000;
}
