using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NSec.Cryptography;

namespace ConfigSigner;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return 1;
            }

            var cmd = args[0].Trim().ToLowerInvariant();
            return cmd switch
            {
                "gen-keypair" => GenKeypair(),
                "sign" => Sign(args),
                "verify" => Verify(args),
                "--help" or "-h" or "/?" => Help(),
                _ => Unknown(cmd)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 2;
        }
    }

    private static int Help()
    {
        PrintHelp();
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
ConfigSigner (Ed25519) - SAAIA

USAGE
  ConfigSigner gen-keypair
    -> prints:
       PRIVATE_KEY_BASE64 (RawPrivateKey, 32 bytes)
       PUBLIC_KEY_BASE64  (RawPublicKey,  32 bytes)

  ConfigSigner sign <configPath> <privateKeyBase64|@privateKeyFile> [signatureOutPath]
    -> writes Base64 signature to signatureOutPath
    -> default signatureOutPath:
       <same folder>\<config filename without extension>.sig
       Example: deployment.config.json -> deployment.config.sig

  ConfigSigner verify <configPath> <signatureBase64|signatureFilePath|@signatureFile> <publicKeyBase64|@publicKeyFile>
    -> exits 0 if OK, 3 if invalid signature

NOTES
  - Using @file reads the file content (trimmed) as Base64.
  - Do NOT store private keys in the repo.
");
    }

    private static string ReadArgOrFile(string value)
    {
        value = (value ?? "").Trim();
        if (value.StartsWith("@"))
        {
            var path = value.Substring(1).Trim().Trim('"');
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");
            return File.ReadAllText(path, Encoding.UTF8).Trim();
        }
        return value;
    }

    private static string DefaultSigPath(string configPath)
    {
        var dir = Path.GetDirectoryName(configPath);
        var name = Path.GetFileNameWithoutExtension(configPath);
        var file = $"{name}.sig";
        return string.IsNullOrEmpty(dir) ? file : Path.Combine(dir, file);
    }

    private static int GenKeypair()
    {
        var kp = new Key(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var priv = kp.Export(KeyBlobFormat.RawPrivateKey);
        var pub = kp.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        Console.WriteLine("PRIVATE_KEY_BASE64=" + Convert.ToBase64String(priv));
        Console.WriteLine("PUBLIC_KEY_BASE64=" + Convert.ToBase64String(pub));
        return 0;
    }

    private static int Sign(string[] args)
    {
        if (args.Length < 3)
        {
            PrintHelp();
            return 1;
        }

        var configPath = args[1].Trim().Trim('"');
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Config file not found: {configPath}");

        var privB64 = ReadArgOrFile(args[2]);
        var sigOut = (args.Length >= 4 && !string.IsNullOrWhiteSpace(args[3]))
            ? args[3].Trim().Trim('"')
            : DefaultSigPath(configPath);

        ValidateRenderedConfig(configPath);

        var privBytes = Convert.FromBase64String(privB64.Trim());
        var key = Key.Import(SignatureAlgorithm.Ed25519, privBytes, KeyBlobFormat.RawPrivateKey);

        var cfgBytes = File.ReadAllBytes(configPath);
        var sig = SignatureAlgorithm.Ed25519.Sign(key, cfgBytes);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(sigOut))!);
        File.WriteAllText(sigOut, Convert.ToBase64String(sig), Encoding.UTF8);

        Console.WriteLine($"OK: signature written to {sigOut}");
        return 0;
    }

    private static void ValidateRenderedConfig(string configPath)
    {
        var text = File.ReadAllText(configPath, Encoding.UTF8);
        using (JsonDocument.Parse(text))
        {
        }

        var placeholder = Regex.Match(text, "__[A-Z0-9_]+__");
        if (placeholder.Success)
            throw new InvalidOperationException($"Unresolved config placeholder: {placeholder.Value}");
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 4)
        {
            PrintHelp();
            return 1;
        }

        var configPath = args[1].Trim().Trim('"');
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Config file not found: {configPath}");

        var sigArg = args[2].Trim();
        byte[] sigBytes;

        if (sigArg.StartsWith("@"))
        {
            sigBytes = Convert.FromBase64String(ReadArgOrFile(sigArg));
        }
        else if (File.Exists(sigArg.Trim('"')))
        {
            var sigPath = sigArg.Trim().Trim('"');
            var sigText = File.ReadAllText(sigPath, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(sigText))
                throw new InvalidOperationException($"Empty signature file: {sigPath}");
            sigBytes = Convert.FromBase64String(sigText);
        }
        else
        {
            sigBytes = Convert.FromBase64String(sigArg);
        }

        var pubB64 = ReadArgOrFile(args[3]);
        var pubBytes = Convert.FromBase64String(pubB64.Trim());
        var pub = PublicKey.Import(SignatureAlgorithm.Ed25519, pubBytes, KeyBlobFormat.RawPublicKey);

        var cfgBytes = File.ReadAllBytes(configPath);
        var ok = SignatureAlgorithm.Ed25519.Verify(pub, cfgBytes, sigBytes);

        Console.WriteLine(ok ? "VALID" : "INVALID");
        return ok ? 0 : 3;
    }
}
