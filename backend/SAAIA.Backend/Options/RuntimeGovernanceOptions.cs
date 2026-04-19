namespace SAAIA.Backend;

sealed class RuntimeGovernanceOptions
{
    public int WarmupPassCount { get; set; } = 3;
    public string DefaultProfileKey { get; set; } = "default-local";
    public bool SelectQualifiedCoreRetrieval { get; set; } = true;
    public bool AutoAuthorizeQualifiedCoreRetrieval { get; set; } = true;
    public bool AutoSelectQualifiedCoreRetrieval { get; set; } = true;
}
