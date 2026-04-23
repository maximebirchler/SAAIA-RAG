using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class SourcesCardsControl : UserControl
{
    public SourcesCardsControl()
    {
        InitializeComponent();
        SourcesHeaderText.Text = ST("Sources", "Sources", "Fuentes", "Fontes", "Quellen", "Fonti");
        Visibility = Visibility.Collapsed;
    }

    public IList<SourceCard> Items
    {
        get => (IList<SourceCard>)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(IList<SourceCard>),
            typeof(SourcesCardsControl),
            new PropertyMetadata(new List<SourceCard>(), OnItemsChanged));

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (SourcesCardsControl)d;

        var list = e.NewValue as IList<SourceCard> ?? new List<SourceCard>();
        self.ItemsHost.ItemsSource = list;

        self.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not SourceCard s)
            return;

        var page = s.PageStart ?? s.PageEnd;
        var result = await DocumentLauncher.TryOpenAsync(s.DocPath, page);
        if (result.Success)
            return;

        await ShowErrorAsync(
            result.ErrorTitle ?? ST("Impossible d'ouvrir le fichier", "Could not open the file", "No se pudo abrir el archivo", "Nao foi possivel abrir o ficheiro", "Datei konnte nicht geoeffnet werden", "Impossibile aprire il file"),
            result.ErrorMessage ?? ST("Erreur inconnue.", "Unknown error.", "Error desconocido.", "Erro desconhecido.", "Unbekannter Fehler.", "Errore sconosciuto."));
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = ClientUiText.Get("dialog.close", AppSettings.Load().UiLanguage),
                XamlRoot = this.XamlRoot
            };
            await dlg.ShowAsync();
        }
        catch
        {
            // Edge case: no XamlRoot available. Avoid crashing while keeping the UI responsive.
        }
    }

    private void OpenButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            button.Content = ST("Ouvrir", "Open", "Abrir", "Abrir", "Oeffnen", "Apri");
    }

    private static string ST(string fr, string en, string es, string pt, string de, string it)
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
