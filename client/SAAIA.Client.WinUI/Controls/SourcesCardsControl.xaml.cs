using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using Windows.Storage;
using Windows.System;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class SourcesCardsControl : UserControl
{
    public SourcesCardsControl()
    {
        InitializeComponent();
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
        if (sender is not Button b || b.Tag is not SourceCard s) return;

        var resolved = DocumentPathResolver.Resolve(s.DocPath);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            await ShowErrorAsync(
                "Fichier introuvable",
                $"docPath (backend): {s.DocPath}\nDocumentsRoot (client): {DocumentPathResolver.GetDocumentsRoot()}");
            return;
        }

        var page = s.PageStart ?? s.PageEnd;

        try
        {
            // 1) Best-effort: PDF + page (Edge supporte souvent #page=)
            if (page is not null && string.Equals(Path.GetExtension(resolved), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var fileUri = new Uri(resolved); // file:///C:/... (avec espaces encodés)
                var uriWithPage = new Uri(fileUri.AbsoluteUri + $"#page={page}");

                var ok = await Launcher.LaunchUriAsync(uriWithPage);
                if (ok) return;
            }

            // 2) Fallback: ouvrir le fichier directement (robuste)
            var file = await StorageFile.GetFileFromPathAsync(resolved);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Impossible d'ouvrir le fichier", ex.Message);
        }
    }

    private async System.Threading.Tasks.Task ShowErrorAsync(string title, string message)
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dlg.ShowAsync();
        }
        catch
        {
            // si pas de XamlRoot dispo dans un cas edge, on évite de crasher
        }
    }
}
