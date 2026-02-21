using System;
using System.IO;
using System.Linq;

namespace SAAIA.Client.WinUI.Services;

internal static class DocumentPathResolver
{
    // Optionnel : override via variable d'environnement
    // setx SAAIA_DOCUMENTS_ROOT "C:\...\documents"
    private const string EnvVar = "SAAIA_DOCUMENTS_ROOT";

    // Optionnel : si l’install “prod” est hors repo (M4),
    // le client peut aussi déduire le dossier documents depuis SAAIA_INSTALL_ROOT.
    // setx SAAIA_INSTALL_ROOT "C:/SAAIA"
    private const string EnvInstallRoot = "SAAIA_INSTALL_ROOT";

    private static string? NormalizeEnvPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        p = p.Trim().Trim('"').Trim(''');
        return p;
    }

    public static string GetDocumentsRoot()
    {
        // 1) ENV override explicite
        var env = NormalizeEnvPath(Environment.GetEnvironmentVariable(EnvVar));
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return Path.GetFullPath(env);

        // 2) Si SAAIA_INSTALL_ROOT pointe sur une installation prod (M4), utiliser <root>\documents si présent
        var installRoot = NormalizeEnvPath(Environment.GetEnvironmentVariable(EnvInstallRoot));
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(installRoot, "documents"));
                if (Directory.Exists(candidate))
                    return candidate;
            }
            catch { /* ignore */ }
        }

        // 3) Heuristique : remonter depuis le dossier de l'exe et chercher un dossier "documents"
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "documents");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);

            dir = dir.Parent;
        }

        // 4) fallback (peut ne pas exister)
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "documents"));
    }

    public static string? Resolve(string? docPath)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            return null;

        // Normalise
        var p = docPath.Trim().TrimStart('\', '/')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\', Path.DirectorySeparatorChar);

        // Si déjà absolu et existe -> OK
        if (Path.IsPathRooted(p) && File.Exists(p))
            return Path.GetFullPath(p);

        var root = GetDocumentsRoot();

        // Essai 1 : root + docPath relatif
        var candidate = Path.GetFullPath(Path.Combine(root, p));
        if (File.Exists(candidate))
            return candidate;

        // Essai 2 : root + nom du fichier (si le backend ne renvoie pas les sous-dossiers)
        var fileName = Path.GetFileName(p);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var candidate2 = Path.GetFullPath(Path.Combine(root, fileName));
            if (File.Exists(candidate2))
                return candidate2;

            // Essai 3 : recherche dans documents (best effort)
            try
            {
                var found = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found) && File.Exists(found))
                    return found;
            }
            catch { }
        }

        return null;
    }
}
