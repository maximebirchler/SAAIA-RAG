using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private async void LocalLlmRuntimeDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShowLocalLlmRuntimeDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Diagnostic runtime impossible: " + ex.Message;
        }
    }

    private async Task ShowLocalLlmRuntimeDiagnosticsAsync()
    {
        var settings = ReadLocalLlmSettingsFromUi();
        var diagnostics = await LocalLlmRuntimeDiagnosticsService.EvaluateAsync(settings).ConfigureAwait(true);

        FrameworkElement BuildMetricTile(string label, string value)
            => BuildDialogSurfaceCard(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = UseLightPalette() ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7)
                    },
                    new TextBlock
                    {
                        Text = value,
                        FontSize = 22,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
                        TextWrapping = TextWrapping.WrapWholeWords
                    }
                }
            }, new Thickness(14));

        FrameworkElement BuildField(string label, string? value)
            => BuildDialogSurfaceCard(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    BuildDialogFieldLabel(label),
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(value) ? "-" : value,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        IsTextSelectionEnabled = true,
                        Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
                    }
                }
            }, new Thickness(14));

        string bannerText;
        var bannerPositive = false;
        if (diagnostics.UpgradeRequired)
        {
            bannerText = $"Le modele courant requiert un runtime plus recent ({diagnostics.RequiredBuild ?? "inconnu"}).";
        }
        else if (string.Equals(diagnostics.ActiveState, "pending_qualification", StringComparison.OrdinalIgnoreCase))
        {
            bannerText = $"Le runtime actif ({diagnostics.ActiveBuild ?? "inconnu"}) attend encore sa qualification warmup.";
        }
        else if (diagnostics.LatestWarmupStatus is WarmupGateStatus.FailBlock or WarmupGateStatus.FailFallback)
        {
            bannerText = "Le dernier warmup a echoue. Le runtime local doit etre reverifie.";
        }
        else
        {
            bannerText = "Le runtime local est compatible avec le modele courant.";
            bannerPositive = true;
        }

        var metricsGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var i = 0; i < 3; i++)
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var metricTiles = new[]
        {
            BuildMetricTile("Runtime", diagnostics.RuntimeLabel),
            BuildMetricTile("Build actif", diagnostics.ActiveBuild ?? "inconnu"),
            BuildMetricTile("Build requis", diagnostics.RequiredBuild ?? "aucun"),
            BuildMetricTile("Etat", ResolveRuntimeStateLabel(diagnostics.ActiveState)),
            BuildMetricTile("Build precedent", diagnostics.PreviousBuild ?? "-"),
            BuildMetricTile("Warmup", ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus))
        };

        for (var index = 0; index < metricTiles.Length; index++)
        {
            Grid.SetColumn(metricTiles[index], index % 3);
            Grid.SetRow(metricTiles[index], index / 3);
            metricsGrid.Children.Add(metricTiles[index]);
        }

        var detailsGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++)
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var detailCards = new[]
        {
            BuildField("Modele", diagnostics.ModelId),
            BuildField("Famille GGUF", diagnostics.ModelFamily),
            BuildField("Profil qualifie", diagnostics.QualifiedProfileId),
            BuildField("Policy flash-attn", ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn)),
            BuildField("Exe actif", diagnostics.ActiveExePath),
            BuildField("Manifest runtime", diagnostics.ActiveManifestPath),
            BuildField("Derniere raison warmup", diagnostics.LatestWarmupReason),
            BuildField("Compatibilite", diagnostics.CompatibilityReason)
        };

        for (var index = 0; index < detailCards.Length; index++)
        {
            Grid.SetColumn(detailCards[index], index % 2);
            Grid.SetRow(detailCards[index], index / 2);
            detailsGrid.Children.Add(detailCards[index]);
        }

        var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", _appSettings.UiLanguage), primary: true);
        var dialogSize = GetDialogMaxSize(900, 760, horizontalMargin: 72, verticalMargin: 96);
        var shell = BuildScrollableDialogShell(
            "Runtime",
            "Diagnostic runtime local",
            "Etat du runtime actif, compatibilite modele/runtime et dernier warmup.",
            new UIElement[]
            {
                BuildDialogInfoBanner(bannerText, bannerPositive),
                BuildDialogSurfaceCard(metricsGrid, new Thickness(12)),
                BuildDialogSurfaceCard(detailsGrid, new Thickness(12))
            },
            BuildDialogFooter(closeButton),
            dialogSize.Width,
            dialogSize.Height);

        OverlayDialogSession? overlay = null;
        closeButton.Click += (_, _) => overlay?.Close();
        overlay = ShowOverlayDialog(
            shell,
            resizeHandler: _ =>
            {
                var size = GetDialogMaxSize(900, 760, horizontalMargin: 72, verticalMargin: 96);
                shell.MaxWidth = size.Width;
                shell.MaxHeight = size.Height;
            });

        await overlay.Completion;
    }

    private static string ResolveRuntimeStateLabel(string? state)
        => state switch
        {
            "qualified" => "qualifie",
            "pending_qualification" => "qualification en attente",
            null or "" => "legacy / non suivi",
            _ => state
        };

    private static string ResolveWarmupStateLabel(WarmupGateStatus? status)
        => status switch
        {
            WarmupGateStatus.Pass => "pass",
            WarmupGateStatus.PassDegraded => "degrade",
            WarmupGateStatus.FailBlock => "blocage",
            WarmupGateStatus.FailFallback => "fallback",
            null => "aucun",
            _ => status?.ToString() ?? "aucun"
        };

    private static string ResolveFlashAttnPolicyLabel(bool? forcedFlashAttn)
        => forcedFlashAttn switch
        {
            true => "force on",
            false => "force off",
            null => "auto / aucune surcharge"
        };
}
