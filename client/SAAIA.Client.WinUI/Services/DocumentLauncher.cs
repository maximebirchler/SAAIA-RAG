using System;
using System.IO;
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

    public static async Task<OpenDocumentResult> TryOpenAsync(string? docPath, int? page = null)
    {
        if (string.IsNullOrWhiteSpace(docPath))
        {
            return new OpenDocumentResult(
                Success: false,
                ResolvedPath: null,
                ErrorTitle: DT("Fichier introuvable", "File not found", "Archivo no encontrado", "Ficheiro nao encontrado", "Datei nicht gefunden", "File non trovato"),
                ErrorMessage: DT("docPath vide.", "Empty docPath.", "docPath vacio.", "docPath vazio.", "Leerer docPath.", "docPath vuoto."));
        }

        var resolved = DocumentPathResolver.Resolve(docPath);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            var details = $"docPath (backend): {docPath}\nDocumentsRoot (client): {DocumentPathResolver.GetDocumentsRoot()}";
            return new OpenDocumentResult(
                Success: false,
                ResolvedPath: null,
                ErrorTitle: DT("Fichier introuvable", "File not found", "Archivo no encontrado", "Ficheiro nao encontrado", "Datei nicht gefunden", "File non trovato"),
                ErrorMessage: details);
        }

        try
        {
            if (page is > 0 && string.Equals(Path.GetExtension(resolved), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var fileUri = new Uri(new Uri("file:///"), resolved.Replace('\\', '/'));
                var uriWithPage = new Uri(fileUri.AbsoluteUri + $"#page={page.Value}");

                var ok = await Launcher.LaunchUriAsync(uriWithPage);
                if (ok)
                {
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
                return new OpenDocumentResult(
                    Success: true,
                    ResolvedPath: resolved,
                    ErrorTitle: null,
                    ErrorMessage: null);
            }

            return new OpenDocumentResult(
                Success: false,
                ResolvedPath: resolved,
                ErrorTitle: DT("Impossible d'ouvrir le fichier", "Could not open the file", "No se pudo abrir el archivo", "Nao foi possivel abrir o ficheiro", "Datei konnte nicht geoeffnet werden", "Impossibile aprire il file"),
                ErrorMessage: DT("Aucune application associee n'a pu ouvrir ce fichier.", "No associated application could open this file.", "Ninguna aplicacion asociada pudo abrir este archivo.", "Nenhuma aplicacao associada conseguiu abrir este ficheiro.", "Keine zugeordnete Anwendung konnte diese Datei oeffnen.", "Nessuna applicazione associata ha potuto aprire questo file."));
        }
        catch (Exception ex)
        {
            return new OpenDocumentResult(
                Success: false,
                ResolvedPath: resolved,
                ErrorTitle: DT("Impossible d'ouvrir le fichier", "Could not open the file", "No se pudo abrir el archivo", "Nao foi possivel abrir o ficheiro", "Datei konnte nicht geoeffnet werden", "Impossibile aprire il file"),
                ErrorMessage: ex.Message);
        }
    }

    private static string DT(string fr, string en, string es, string pt, string de, string it)
    {
        var lang = ClientUiText.NormalizeLanguage(AppSettings.Load().UiLanguage);
        return lang switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };
    }
}
