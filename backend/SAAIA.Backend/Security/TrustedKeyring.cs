namespace SAAIA.Backend.Security;

internal static class TrustedKeyring
{
    // Clés publiques Ed25519 (Base64, RawPublicKey 32 bytes)
    // Ajoute ici les clés que TU considères comme “de confiance”.
    internal static readonly string[] PublicKeysBase64 = new[]
    {
        // Valeur initiale = celle qui était dans appsettings.json
        "8LNTQpr23+P3YfN5RHz21zD1d75GOqc/f17vAipLTZ0="
    };
}
