using System.Threading.Tasks;
using System.Text;

using Microsoft.UI.Xaml.Controls;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class LinkifiedTextBlock
{
    private async Task OpenAsync(string docPath, int page)
    {
        try
        {
            var result = await DocumentLauncher.TryOpenAsync(docPath, page);
            if (result.Success)
                return;

            await ShowOpenErrorAsync(
                result.ErrorTitle ?? LocalText("Impossible d'ouvrir le fichier", "Could not open the file", "No se pudo abrir el archivo", "Não foi possível abrir o ficheiro", "Datei konnte nicht geöffnet werden", "Impossibile aprire il file"),
                result.ErrorMessage ?? LocalText("Erreur inconnue.", "Unknown error.", "Error desconocido.", "Erro desconhecido.", "Unbekannter Fehler.", "Errore sconosciuto."));
        }
        catch (System.Exception ex)
        {
            ClientLog.Exception("LinkifiedTextBlock.OpenDocument", ex);
            await ShowOpenErrorAsync(
                LocalText("Impossible d'ouvrir le fichier", "Could not open the file", "No se pudo abrir el archivo", "Não foi possível abrir o ficheiro", "Datei konnte nicht geöffnet werden", "Impossibile aprire il file"),
                BuildOpenLinkFailureUserMessage());
        }
    }

    private static string BuildOpenLinkFailureUserMessage()
        => LocalText(
            "Le document n'a pas pu etre ouvert depuis le lien. Essaie de l'ouvrir depuis la carte source ou verifie l'application PDF par defaut.",
            "The document could not be opened from the link. Try opening it from the source card or check the default PDF application.",
            "No se pudo abrir el documento desde el enlace. Intenta abrirlo desde la tarjeta de fuente o revisa la aplicacion PDF predeterminada.",
            "Nao foi possivel abrir o documento a partir da ligacao. Tenta abri-lo a partir do cartao de fonte ou verifica a aplicacao PDF predefinida.",
            "Das Dokument konnte ueber den Link nicht geoeffnet werden. Oeffne es ueber die Quellenkarte oder pruefe die Standard-PDF-Anwendung.",
            "Non e stato possibile aprire il documento dal link. Prova ad aprirlo dalla scheda fonte o controlla l'app PDF predefinita.");

    private async Task ShowOpenErrorAsync(string title, string message)
    {
        try
        {
            var lang = CurrentUiLanguage();
            var dlg = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = ClientUiText.Get("dialog.close", lang),
                XamlRoot = XamlRoot
            };
            await dlg.ShowAsync();
        }
        catch
        {
            // In rare early-render cases XamlRoot is unavailable. The launcher failure is non-fatal.
        }
    }

    private static string CurrentUiLanguage()
        => ClientUiText.NormalizeLanguage(AppSettings.Load().UiLanguage);

    private static string LocalText(string fr, string en, string es, string pt, string de, string it)
    {
        var value = CurrentUiLanguage() switch
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
            || (!value.Contains('\u00c3', System.StringComparison.Ordinal) && !value.Contains('\u00c2', System.StringComparison.Ordinal)))
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
}
