using System.Text;
using NSec.Cryptography;

namespace SAAIA.Backend.Security;

public static class SignedConfigLoader
{
    public static void AddSignedDeploymentConfig(WebApplicationBuilder builder)
    {
        var opt = new ConfigSignatureOptions();
        builder.Configuration.GetSection("ConfigSignature").Bind(opt);

        var configPath = ResolvePath(builder.Environment.ContentRootPath, opt.ConfigPath);
        var sigPath = ResolvePath(builder.Environment.ContentRootPath, opt.SignaturePath);

        // Expose status for /ready (non-sensitive).
        // Note: if verification fails, app won't start.
        SignedConfigStatus? statusToRegister = null;

        if (!File.Exists(configPath))
            throw new InvalidOperationException($"Missing signed deployment config: {configPath}");

        if (!File.Exists(sigPath))
        {
            if (builder.Environment.IsDevelopment() && opt.AllowUnsignedInDevelopment)
            {
                statusToRegister = new SignedConfigStatus
                {
                    ConfigPath = configPath,
                    SignaturePath = sigPath,
                    SignaturePresent = false,
                    Verified = false,
                    AllowUnsignedInDevelopment = opt.AllowUnsignedInDevelopment,
                    Mode = "unsigned-dev"
                };
                builder.Services.AddSingleton(statusToRegister);

                builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: false);
                return;
            }

            throw new InvalidOperationException($"Missing signature file: {sigPath}");
        }

        if (TrustedKeyring.PublicKeysBase64 is null || TrustedKeyring.PublicKeysBase64.Length == 0)
            throw new InvalidOperationException("TrustedKeyring.PublicKeysBase64 is empty.");

        var cfgBytes = File.ReadAllBytes(configPath);
        var sigText = File.ReadAllText(sigPath, Encoding.UTF8).Trim();
        if (string.IsNullOrWhiteSpace(sigText))
            throw new InvalidOperationException($"Empty signature file: {sigPath}");

        var sig = Convert.FromBase64String(sigText);

        var ok = TrustedKeyring.PublicKeysBase64.Any(pk64 =>
        {
            if (string.IsNullOrWhiteSpace(pk64)) return false;
            try
            {
                var pkBytes = Convert.FromBase64String(pk64.Trim());
                var pub = PublicKey.Import(SignatureAlgorithm.Ed25519, pkBytes, KeyBlobFormat.RawPublicKey);
                return SignatureAlgorithm.Ed25519.Verify(pub, cfgBytes, sig);
            }
            catch
            {
                return false;
            }
        });

        if (!ok)
            throw new InvalidOperationException("deployment.config.json signature verification failed.");

        statusToRegister = new SignedConfigStatus
        {
            ConfigPath = configPath,
            SignaturePath = sigPath,
            SignaturePresent = true,
            Verified = true,
            AllowUnsignedInDevelopment = opt.AllowUnsignedInDevelopment,
            Mode = "signed"
        };
        builder.Services.AddSingleton(statusToRegister);

        using var ms = new MemoryStream(cfgBytes);
        builder.Configuration.AddJsonStream(ms);
    }

    private static string ResolvePath(string contentRoot, string pathOrRelative)
        => Path.IsPathRooted(pathOrRelative)
            ? pathOrRelative
            : Path.GetFullPath(Path.Combine(contentRoot, pathOrRelative));
}
