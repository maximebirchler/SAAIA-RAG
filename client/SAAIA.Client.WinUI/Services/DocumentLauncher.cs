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
                ErrorTitle: "Fichier introuvable",
                ErrorMessage: "docPath vide.");
        }

        var resolved = DocumentPathResolver.Resolve(docPath);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return new OpenDocumentResult(
                Success: false,
                ResolvedPath: null,
                ErrorTitle: "Fichier introuvable",
                ErrorMessage: $"docPath (backend): {docPath}\nDocumentsRoot (client): {DocumentPathResolver.GetDocumentsRoot()}");
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
                ErrorTitle: "Impossible d'ouvrir le fichier",
                ErrorMessage: "Aucune application associée n'a pu ouvrir ce fichier.");
        }
        catch (Exception ex)
        {
            return new OpenDocumentResult(
                Success: false,
                ResolvedPath: resolved,
                ErrorTitle: "Impossible d'ouvrir le fichier",
                ErrorMessage: ex.Message);
        }
    }
}
