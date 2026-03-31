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
            result.ErrorTitle ?? "Impossible d'ouvrir le fichier",
            result.ErrorMessage ?? "Erreur inconnue.");
    }

    private async Task ShowErrorAsync(string title, string message)
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
            // Edge case: no XamlRoot available. Avoid crashing while keeping the UI responsive.
        }
    }
}
