internal sealed class VectorGcOptions
{
    public bool Enabled { get; set; }
    public bool AllowCurrentTarget { get; set; } = true;
    public bool RequireTargetRevision { get; set; } = true;
    public int PollSeconds { get; set; } = 30;
    public int MaxPlansPerRound { get; set; } = 4;
    public int MaxPointsPerBatch { get; set; } = 128;
    public int RoundTimeoutSeconds { get; set; } = 30;
    public int BatchTimeoutSeconds { get; set; } = 10;
    public int RecheckSeconds { get; set; } = 3600;
    public int FailureBackoffSeconds { get; set; } = 300;
    public List<VectorGcApprovedTarget> ApprovedTargets { get; set; } = [];
}

internal sealed class VectorGcApprovedTarget
{
    public string Endpoint { get; set; } = "";
    public string Collection { get; set; } = "";
    public string Revision { get; set; } = "";
    public string? ApiKeyRef { get; set; }
    public string AuthMode { get; set; } = "api-key";
    public bool AllowUnauthenticated { get; set; }
}
