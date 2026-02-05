namespace SAAIA.Backend.Security;

public sealed class ConfigSignatureOptions
{
    public string ConfigPath { get; set; } = "deployment.config.json";
    public string SignaturePath { get; set; } = "deployment.config.sig";

    public bool AllowUnsignedInDevelopment { get; set; } = false;
}
