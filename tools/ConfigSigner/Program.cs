using System.Text;
using NSec.Cryptography;

namespace ConfigSigner;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 2;
        }

        var cmd = args[0].Trim().ToLowerInvariant();
        try
        {
            return cmd switch
            {
                "gen-keypair" => GenKeypair(),
                "sign" => Sign(args),
                "verify" => Verify(args),
                _ => Unknown(cmd)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
ConfigSigner (Ed25519)
Commands:
  gen-keypair
      Prints:
        PUBLIC_KEY_BASE64=...
        PRIVATE_KEY_BASE64=...

  sign <configPath> <privateKeyBase64> [signatureOutPath]
      Writes Base64 signature to signatureOutPath (default: <configPath>.sig)

  verify <configPath> <signatureBase64|signatureFilePath> <publicKeyBase64>
      Returns 0 if valid, 1 if invalid
");
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine("Unknown command: " + cmd);
        PrintHelp();
        return 2;
    }

    private static int GenKeypair()
    {
        var kp = new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        };

        using var key = new Key(SignatureAlgorithm.Ed25519, kp);

        var pub = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var priv = key.Export(KeyBlobFormat.RawPrivateKey);

        Console.WriteLine("PUBLIC_KEY_BASE64=" + Convert.ToBase64String(pub));
        Console.WriteLine("PRIVATE_KEY_BASE64=" + Convert.ToBase64String(priv));
        return 0;
    }

    private static int Sign(string[] args)
    {
        if (args.Length < 3)
        {
            PrintHelp();
            return 2;
        }

        var configPath = args[1];
        var privB64 = args[2];
        var sigOut = args.Length >= 4 ? args[3] : (configPath + ".sig");

        var cfgBytes = File.ReadAllBytes(configPath);
        var priv = Convert.FromBase64String(privB64);

        using var key = Key.Import(SignatureAlgorithm.Ed25519, priv, KeyBlobFormat.RawPrivateKey);

        var sig = SignatureAlgorithm.Ed25519.Sign(key, cfgBytes);
        File.WriteAllText(sigOut, Convert.ToBase64String(sig), Encoding.UTF8);

        Console.WriteLine("OK: wrote signature to " + sigOut);
        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 4)
        {
            PrintHelp();
            return 2;
        }

        var configPath = args[1];
        var sigArg = args[2];
        var pubB64 = args[3];

        var cfgBytes = File.ReadAllBytes(configPath);

        byte[] sig;
        if (File.Exists(sigArg))
            sig = Convert.FromBase64String(File.ReadAllText(sigArg, Encoding.UTF8).Trim());
        else
            sig = Convert.FromBase64String(sigArg.Trim());

        var pub = Convert.FromBase64String(pubB64);
        var pk = PublicKey.Import(SignatureAlgorithm.Ed25519, pub, KeyBlobFormat.RawPublicKey);

        var ok = SignatureAlgorithm.Ed25519.Verify(pk, cfgBytes, sig);
        Console.WriteLine(ok ? "VALID" : "INVALID");
        return ok ? 0 : 1;
    }
}
