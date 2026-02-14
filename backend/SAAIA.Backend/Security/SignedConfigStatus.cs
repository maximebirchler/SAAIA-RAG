namespace SAAIA.Backend.Security;

/// <summary>
/// Exposes (non-sensitive) information about deployment config signature loading.
/// Used by /ready to report whether config is signed/verified.
/// </summary>
public sealed class SignedConfigStatus
{
    public string ConfigPath { get; init; } = "";
    public string SignaturePath { get; init; } = "";

    /// <summary>Signature file exists.</summary>
    public bool SignaturePresent { get; init; }

    /// <summary>Signature verification succeeded. In unsigned-dev mode, this is false.</summary>
    public bool Verified { get; init; }

    /// <summary>Whether unsigned config is allowed in Development.</summary>
    public bool AllowUnsignedInDevelopment { get; init; }

    /// <summary>"signed" | "unsigned-dev"</summary>
    public string Mode { get; init; } = "signed";
}
