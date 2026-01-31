namespace SAAIA.Backend.Options;

public sealed class ConfigSignatureOptions
{
    public string ConfigPath { get; set; } = "deployment.config.json";
    public string SignaturePath { get; set; } = "deployment.config.sig";

    /// <summary>Clés publiques Ed25519 (Base64, 32 bytes RawPublicKey).</summary>
    public string[] TrustedPublicKeysBase64 { get; set; } = Array.Empty<string>();

    /// <summary>Uniquement en Development : autorise démarrage sans signature.</summary>
    public bool AllowUnsignedInDevelopment { get; set; } = false;
}
