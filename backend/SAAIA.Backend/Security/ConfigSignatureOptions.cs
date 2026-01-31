namespace SAAIA.Backend.Security;

public sealed class ConfigSignatureOptions
{
    public string ConfigPath { get; set; } = "deployment.config.json";
    public string SignaturePath { get; set; } = "deployment.config.sig";

    // Base64 RawPublicKey Ed25519 (32 bytes)
    public string[] TrustedPublicKeysBase64 { get; set; } = Array.Empty<string>();

    public bool AllowUnsignedInDevelopment { get; set; } = false;
}
