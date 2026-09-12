using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Windows.Storage;
using Windows.System;

namespace SAAIA.Client.WinUI.Services;

internal static class DocumentLauncher
{
    internal readonly record struct OpenDocumentResult(
        bool Success,
        string? ResolvedPath,
        string? ErrorTitle,
        string? ErrorMessage);

    public static async Task<OpenDocumentResult> TryOpenAsync(
        string? docPath,
        int? page = null,
        string? sourceHash = null,
        string? revisionId = null,
        string? chunkId = null,
        string? anchorId = null,
        string? contentCardId = null,
        bool requireExactSourceHash = false)
    {
        if (string.IsNullOrWhiteSpace(docPath))
        {
            return new OpenDocumentResult(
                Success: false,
                ResolvedPath: null,
                ErrorTitle: DT("Fichier introuvable", "File not found", "Archivo no encontrado", "Ficheiro não encontrado", "Datei nicht gefunden", "File non trovato"),
                ErrorMessage: DT(
                    "La source ne contient pas de chemin de fichier exploitable.",
                    "The source does not contain a usable file path.",
                    "La fuente no contiene una ruta de archivo utilizable.",
                    "A fonte não contém um caminho de ficheiro utilizável.",
                    "Die Quelle enthält keinen nutzbaren Dateipfad.",
                    "La fonte non contiene un percorso file utilizzabile."));
        }

        ClientLog.Info(
            "DocumentLauncher.Open request"
            + $"; revision={revisionId ?? "(none)"}"
            + $"; chunk={chunkId ?? "(none)"}"
            + $"; anchor={anchorId ?? "(none)"}"
            + $"; contentCard={contentCardId ?? "(none)"}"
            + $"; exactHashRequired={requireExactSourceHash}");
        var resolved = await Task.Run(() => requireExactSourceHash
            ? DocumentPathResolver.ResolveExactRevision(docPath, sourceHash)
            : DocumentPathResolver.Resolve(docPath, sourceHash));
        if (string.IsNullOrWhiteSpace(resolved))
        {
            var documentsRoot = await Task.Run(DocumentPathResolver.GetDocumentsRoot);
            var identityDetails = requireExactSourceHash
                ? DT(
                    $"\nRévision indexée : {revisionId ?? "inconnue"}\nLe fichier local doit correspondre exactement à son SHA-256.",
                    $"\nIndexed revision: {revisionId ?? "unknown"}\nThe local file must exactly match its SHA-256.",
                    $"\nRevisión indexada: {revisionId ?? "desconocida"}\nEl archivo local debe coincidir exactamente con su SHA-256.",
                    $"\nRevisão indexada: {revisionId ?? "desconhecida"}\nO ficheiro local deve corresponder exatamente ao seu SHA-256.",
                    $"\nIndexierte Revision: {revisionId ?? "unbekannt"}\nDie lokale Datei muss exakt ihrem SHA-256 entsprechen.",
                    $"\nRevisione indicizzata: {revisionId ?? "sconosciuta"}\nIl file locale deve corrispondere esattamente al suo SHA-256.")
                : string.Empty;
            var details = DT(
                $"Chemin reçu du serveur : {docPath}\nDossier documents configuré sur ce poste : {documentsRoot}",
                $"Path received from the server: {docPath}\nDocuments folder configured on this computer: {documentsRoot}",
                $"Ruta recibida del servidor: {docPath}\nCarpeta de documentos configurada en este equipo: {documentsRoot}",
                $"Caminho recebido do servidor: {docPath}\nPasta de documentos configurada neste posto: {documentsRoot}",
                $"Vom Server erhaltener Pfad: {docPath}\nAuf diesem Gerät konfigurierter Dokumentenordner: {documentsRoot}",
                $"Percorso ricevuto dal server: {docPath}\nCartella documenti configurata su questo computer: {documentsRoot}")
                + identityDetails;
            return new OpenDocumentResult(
                Success: false,
                ResolvedPath: null,
                ErrorTitle: DT("Fichier introuvable", "File not found", "Archivo no encontrado", "Ficheiro não encontrado", "Datei nicht gefunden", "File non trovato"),
                ErrorMessage: details);
        }

        try
        {
            if (page is > 0 && string.Equals(Path.GetExtension(resolved), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                if (await TryOpenPdfAtPageAsync(resolved, page.Value).ConfigureAwait(false))
                {
                    LogOpenSuccess(revisionId, chunkId, requireExactSourceHash);
                    return new OpenDocumentResult(
                        Success: true,
                        ResolvedPath: resolved,
                        ErrorTitle: null,
                        ErrorMessage: null);
                }
            }

            var file = await StorageFile.GetFileFromPathAsync(resolved);
            var fileOpened = await Launcher.LaunchFileAsync(file);
            if (fileOpened)
            {
                LogOpenSuccess(revisionId, chunkId, requireExactSourceHash);
                return new OpenDocumentResult(
                    Success: true,
                    ResolvedPath: resolved,
                    ErrorTitle: null,
                    ErrorMessage: null);
            }

            return new OpenDocumentResult(
                Success: false,
                ResolvedPath: resolved,
                ErrorTitle: DT("Impossible d'ouvrir le fichier", "Could not open the file", "No se pudo abrir el archivo", "Não foi possível abrir o ficheiro", "Datei konnte nicht geöffnet werden", "Impossibile aprire il file"),
                ErrorMessage: DT("Aucune application associée n'a pu ouvrir ce fichier.", "No associated application could open this file.", "Ninguna aplicación asociada pudo abrir este archivo.", "Nenhuma aplicação associada conseguiu abrir este ficheiro.", "Keine zugeordnete Anwendung konnte diese Datei öffnen.", "Nessuna applicazione associata ha potuto aprire questo file."));
        }
        catch (Exception ex)
        {
            ClientLog.Exception("DocumentLauncher.Open", ex);
            return new OpenDocumentResult(
                Success: false,
                ResolvedPath: resolved,
                ErrorTitle: DT("Impossible d'ouvrir le fichier", "Could not open the file", "No se pudo abrir el archivo", "Não foi possível abrir o ficheiro", "Datei konnte nicht geöffnet werden", "Impossibile aprire il file"),
                ErrorMessage: BuildOpenFailureUserMessage());
        }
    }

    private static void LogOpenSuccess(
        string? revisionId,
        string? chunkId,
        bool requireExactSourceHash)
        => ClientLog.Info(
            "DocumentLauncher.Open succeeded"
            + $"; revision={revisionId ?? "(none)"}"
            + $"; chunk={chunkId ?? "(none)"}"
            + $"; exactHashRequired={requireExactSourceHash}");

    private static string BuildOpenFailureUserMessage()
        => DT(
            "Le fichier a bien ete trouve, mais Windows n'a pas pu l'ouvrir. Essaie d'ouvrir le document depuis les sources ou verifie l'application PDF par defaut.",
            "The file was found, but Windows could not open it. Try opening the document from the sources panel or check the default PDF application.",
            "Se encontro el archivo, pero Windows no pudo abrirlo. Intenta abrir el documento desde el panel de fuentes o revisa la aplicacion PDF predeterminada.",
            "O ficheiro foi encontrado, mas o Windows nao conseguiu abri-lo. Tenta abrir o documento a partir do painel de fontes ou verifica a aplicacao PDF predefinida.",
            "Die Datei wurde gefunden, aber Windows konnte sie nicht oeffnen. Oeffne das Dokument ueber die Quellenansicht oder pruefe die Standard-PDF-Anwendung.",
            "Il file e stato trovato, ma Windows non e riuscito ad aprirlo. Prova ad aprire il documento dal pannello delle fonti o controlla l'app PDF predefinita.");

    private static string DT(string fr, string en, string es, string pt, string de, string it)
    {
        var lang = ClientUiText.NormalizeLanguage(AppSettings.Load().UiLanguage);
        var value = lang switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };
        return RepairMojibake(value);
    }

    private static string RepairMojibake(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || (!value.Contains('\u00c3', StringComparison.Ordinal) && !value.Contains('\u00c2', StringComparison.Ordinal)))
        {
            return value;
        }

        try
        {
            return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(value));
        }
        catch
        {
            return value;
        }
    }

    private static async Task<bool> TryOpenPdfAtPageAsync(string resolvedPath, int page)
    {
        var uriWithPage = BuildPdfPageUri(resolvedPath, page);

        // Edge honors PDF page fragments reliably; the default Windows file association often ignores them.
        if (TryLaunchKnownEdge(uriWithPage))
            return true;

        if (await TryLaunchUriAsync(uriWithPage).ConfigureAwait(false))
            return true;

        return await TryLaunchUriAsync("microsoft-edge:" + uriWithPage).ConfigureAwait(false);
    }

    internal static string BuildPdfPageUriForTests(string resolvedPath, int page)
        => BuildPdfPageUri(resolvedPath, page);

    private static string BuildPdfPageUri(string resolvedPath, int page)
    {
        var safePage = Math.Max(1, page).ToString(CultureInfo.InvariantCulture);
        var fileUri = new Uri(Path.GetFullPath(resolvedPath)).AbsoluteUri;
        return $"{fileUri}#page={safePage}";
    }

    private static async Task<bool> TryLaunchUriAsync(string uri)
    {
        try
        {
            return await Launcher.LaunchUriAsync(new Uri(uri));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLaunchKnownEdge(string uriWithPage)
    {
        foreach (var candidate in EnumerateEdgeCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                continue;

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = QuoteArgument(uriWithPage),
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (process is not null)
                    return true;
            }
            catch
            {
                // Try the next known install path, then fall back to Windows URI launching.
            }
        }

        return false;
    }

    private static string[] EnumerateEdgeCandidates()
    {
        var explicitPath = Environment.GetEnvironmentVariable("SAAIA_PDF_BROWSER_PATH");
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new[]
        {
            explicitPath ?? string.Empty,
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe")
        };
    }

    private static string QuoteArgument(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
