using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

/// <summary>
/// User-safe settings dialog (no dangerous configuration).
/// Built in C# to avoid WinUI XamlCompiler fragility.
/// </summary>
internal sealed class UserSettingsDialog : ContentDialog
{
    private readonly AppSettings _original;
    private readonly AppSettings _working;

    private readonly Func<Task>? _repairAssistantAsync;

    private readonly DownloadManager _downloads = new();

    // UI controls (safe)
    private readonly ToggleSwitch _assistantEnabled = new() { Header = "Assistant IA", OnContent = "Activé", OffContent = "Désactivé" };
    private readonly ToggleSwitch _strictMode = new() { Header = "Mode strict", OnContent = "Sources uniquement", OffContent = "Standard" };
    private readonly ComboBox _ragQuality = new() { Header = "Qualité de recherche" };
    private readonly ComboBox _style = new() { Header = "Style de réponse" };
    private readonly ComboBox _length = new() { Header = "Longueur de réponse" };

    // Assistant install / repair
    private readonly TextBlock _assistantStatus = new() { Text = "", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _assistantProgress = new() { Minimum = 0, Maximum = 100, Height = 6, Visibility = Visibility.Collapsed };
    private readonly Button _assistantRepairBtn = new() { Content = "Installer / réparer l'assistant…", HorizontalAlignment = HorizontalAlignment.Left };

    // Support bundle
    private readonly Button _exportBtn = new() { Content = "Exporter diagnostic…", HorizontalAlignment = HorizontalAlignment.Left };

    internal AppSettings UpdatedSettings { get; private set; }

    public UserSettingsDialog(AppSettings settings, Func<Task>? repairAssistantAsync = null)
    {
        _original = settings ?? throw new ArgumentNullException(nameof(settings));
        _working = _original.Clone();
        UpdatedSettings = _original;

        _repairAssistantAsync = repairAssistantAsync;

        Title = "Paramètres";
        PrimaryButtonText = "Appliquer";
        CloseButtonText = "Fermer";
        DefaultButton = ContentDialogButton.Primary;

        // Handle apply
        PrimaryButtonClick += OnPrimaryClicked;

        _assistantEnabled.IsOn = _working.UseLocalLlm;
        _strictMode.IsOn = _working.StrictMode;

        _ragQuality.Items.Add("Rapide");
        _ragQuality.Items.Add("Équilibré");
        _ragQuality.Items.Add("Approfondi");

        _style.Items.Add("Précis");
        _style.Items.Add("Équilibré");
        _style.Items.Add("Créatif");

        _length.Items.Add("Court");
        _length.Items.Add("Standard");
        _length.Items.Add("Long");

        // Apply current selections
        _ragQuality.SelectedIndex = MapRagQualityToIndex(_working.RagQualityPreset);
        _style.SelectedIndex = MapStyleToIndex(_working.Temperature);
        _length.SelectedIndex = MapLenToIndex(_working.MaxOutputTokens);

        _assistantRepairBtn.Click += async (_, _) => await RunRepairAsync().ConfigureAwait(true);
        _exportBtn.Click += async (_, _) => await ExportSupportBundleAsync().ConfigureAwait(true);

        Content = BuildUi();
    }


    // Overload used by some callers: pass XamlRoot explicitly.
    public UserSettingsDialog(AppSettings settings, XamlRoot? xamlRoot)
        : this(settings, xamlRoot, null)
    {
    }

    // Overload used by some callers: pass XamlRoot explicitly + optional repair delegate.
    public UserSettingsDialog(AppSettings settings, XamlRoot? xamlRoot, Func<Task>? repairAssistantAsync = null)
        : this(settings, repairAssistantAsync)
    {
        if (xamlRoot != null)
            XamlRoot = xamlRoot;
    }



    private UIElement BuildUi()
    {
        var root = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 520
        };

        var section1 = Section("Assistant", new UIElement[]
        {
            _assistantEnabled,
            _strictMode,
            _ragQuality,
            _style,
            _length
        });

        var section2 = Section("Dépannage assistant", new UIElement[]
        {
            _assistantRepairBtn,
            _assistantProgress,
            _assistantStatus
        });

        var section3 = Section("Support", new UIElement[]
        {
            _exportBtn,
            new TextBlock
            {
                Text = "Le diagnostic ne contient pas la clé API (elle est masquée).",
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap
            }
        });

        root.Children.Add(section1);
        root.Children.Add(Divider());
        root.Children.Add(section2);
        root.Children.Add(Divider());
        root.Children.Add(section3);

        return new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private static Border Divider() => new()
    {
        Height = 1,
        Opacity = 0.18,
        Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        Margin = new Thickness(0, 4, 0, 4)
    };

    private static UIElement Section(string title, UIElement[] body)
    {
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14
        });

        foreach (var el in body) panel.Children.Add(el);

        return panel;
    }



    private void OnPrimaryClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Apply "safe" settings only
        _working.UseLocalLlm = _assistantEnabled.IsOn;
        _working.StrictMode = _strictMode.IsOn;

        _working.RagQualityPreset = MapIndexToRagQuality(_ragQuality.SelectedIndex);
        _working.Temperature = MapIndexToTemp(_style.SelectedIndex);
        _working.MaxOutputTokens = MapIndexToMaxTokens(_length.SelectedIndex);

        _original.CopyFrom(_working);
        _original.Save();
        UpdatedSettings = _original;
    }

    private static int MapRagQualityToIndex(string? preset)
        => (preset ?? "balanced").ToLowerInvariant() switch
        {
            "quick" => 0,
            "balanced" => 1,
            "deep" => 2,
            _ => 1
        };

    private static string MapIndexToRagQuality(int idx)
        => idx switch
        {
            0 => "quick",
            2 => "deep",
            _ => "balanced"
        };

    private static int MapStyleToIndex(double temp)
    {
        if (temp <= 0.15) return 0;
        if (temp <= 0.55) return 1;
        return 2;
    }

    private static double MapIndexToTemp(int idx)
        => idx switch
        {
            0 => 0.1,   // Précis
            2 => 0.8,   // Créatif
            _ => 0.3    // Équilibré
        };

    private static int MapLenToIndex(int maxTokens)
    {
        if (maxTokens <= 450) return 0;
        if (maxTokens <= 1100) return 1;
        return 2;
    }

    private static int MapIndexToMaxTokens(int idx)
        => idx switch
        {
            0 => 350,   // Court
            2 => 1600,  // Long
            _ => 900    // Standard
        };

    private async Task ExportSupportBundleAsync()
    {
        try
        {
            _exportBtn.IsEnabled = false;
            var zipPath = await SupportBundleBuilder.BuildAsync(_original).ConfigureAwait(true);
            _assistantStatus.Text = "Diagnostic exporté :\n" + zipPath;
            TryOpenFolder(Path.GetDirectoryName(zipPath));
        }
        catch (Exception ex)
        {
            _assistantStatus.Text = "Échec export diagnostic.\n" + ex.Message;
        }
        finally
        {
            _exportBtn.IsEnabled = true;
        }
    }


    private async Task RunRepairAsync()
    {
        // Preferred path: caller-provided repair action (embedded bootstrap in MainWindow).
        if (_repairAssistantAsync is not null)
        {
            _assistantRepairBtn.IsEnabled = false;
            _assistantProgress.Visibility = Visibility.Visible;
            _assistantProgress.IsIndeterminate = true;
            _assistantStatus.Text = "Installation / réparation en cours…";

            try
            {
                await _repairAssistantAsync().ConfigureAwait(true);

                // Post-check
                if (await IsLlmReadyAsync().ConfigureAwait(true))
                    _assistantStatus.Text = "Assistant prêt (LLM disponible).";
                else
                    _assistantStatus.Text = "Terminé. Vérification en cours…";
            }
            catch (Exception ex)
            {
                _assistantStatus.Text = "Échec : " + ex.Message;
            }
            finally
            {
                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantRepairBtn.IsEnabled = true;
            }

            return;
        }

        // Fallback: legacy/script-based repair (dev/test).
        await RepairAssistantAsync().ConfigureAwait(true);
    }

    private async Task RepairAssistantAsync()
    {
        _assistantRepairBtn.IsEnabled = false;
        _assistantProgress.Visibility = Visibility.Visible;
        _assistantProgress.IsIndeterminate = true;
        _assistantStatus.Text = "Vérification de l'assistant…";

        try
        {
            // 1) If LLM endpoint already OK, nothing to do.
            if (await IsLlmReadyAsync().ConfigureAwait(true))
            {
                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantStatus.Text = "Assistant prêt (LLM disponible).";
                return;
            }

            // 2) Prefer install-llm script if present in provisioning.
            var (scriptPath, hasAuto) = TryGetInstallScriptFromProvisioning();
            if (!string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath))
            {
                var ok = await RunInstallScriptElevatedAndWaitAsync(scriptPath).ConfigureAwait(true);
                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantStatus.Text = ok
                    ? "Assistant prêt (LLM installé)."
                    : "Installation annulée ou échouée.";
                return;
            }

            // 3) Fallback: downloads plan (legacy / optional)
            if (Provisioning.TryGetDownloadAssets(out var assets, out var autoFirstRun) && assets.Count > 0)
            {
                _assistantStatus.Text = "Téléchargement des composants…";
                _assistantProgress.IsIndeterminate = false;
                _assistantProgress.Value = 0;

                var prog = new Progress<DownloadManager.ProgressInfo>(p =>
                {
                    if (p.TotalBytes is long tot && tot > 0)
                    {
                        var pct = (double)p.BytesDownloaded / tot * 100.0;
                        _assistantProgress.Value = Math.Clamp(pct, 0, 100);
                    }
                    else
                    {
                        _assistantProgress.IsIndeterminate = true;
                    }

                    _assistantStatus.Text = $"{p.Stage} ({p.AssetId})";
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await _downloads.EnsureAssetsAsync(assets, prog, cts.Token).ConfigureAwait(true);

                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantStatus.Text = "Téléchargement terminé. Redémarrez l'assistant si nécessaire.";
                return;
            }

            _assistantProgress.Visibility = Visibility.Collapsed;
            _assistantStatus.Text = "Aucun moyen automatique trouvé.\n"
                + "Vérifie que le script install-llm.ps1 est présent dans C:\\SAAIA\\deploy\\, ou contacte l'intégrateur.";
        }
        catch (Exception ex)
        {
            _assistantProgress.Visibility = Visibility.Collapsed;
            _assistantStatus.Text = "Erreur pendant la réparation.\n" + ex.Message;
        }
        finally
        {
            _assistantRepairBtn.IsEnabled = true;
        }
    }

    private static (string? ScriptPath, bool AutoInstallOnFirstRun) TryGetInstallScriptFromProvisioning()
    {
        try
        {
            var path = Provisioning.FindProvisioningPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return (null, false);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("llm", out var llm))
                return (null, false);

            string? script = null;
            bool auto = false;

            if (llm.TryGetProperty("installScriptPath", out var p) && p.ValueKind == JsonValueKind.String)
                script = p.GetString();

            if (llm.TryGetProperty("autoInstallOnFirstRun", out var a) && a.ValueKind == JsonValueKind.True)
                auto = true;

            return (script, auto);
        }
        catch
        {
            return (null, false);
        }
    }

    private async Task<bool> RunInstallScriptElevatedAndWaitAsync(string scriptPath)
    {
        _assistantStatus.Text = "Lancement de l'installation (UAC)…";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? ""
            };

            Process.Start(psi);
        }
        catch (Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            _assistantStatus.Text = "Installation annulée (UAC refusé).";
            return false;
        }
        catch (Exception ex)
        {
            _assistantStatus.Text = "Impossible de lancer l'installation.\n" + ex.Message;
            return false;
        }

        // Wait for LLM readiness (models endpoint)
        _assistantStatus.Text = "Attente du démarrage du LLM…";
        _assistantProgress.IsIndeterminate = true;

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsLlmReadyAsync().ConfigureAwait(true))
                return true;

            await Task.Delay(1500).ConfigureAwait(true);
        }

        _assistantStatus.Text = "Timeout : le LLM ne répond pas encore.\n"
            + "Vérifie Docker Desktop et le container saaia-llama.";
        return false;
    }

    private async Task<bool> IsLlmReadyAsync()
    {
        try
        {
            var url = $"http://{_original.LlmHost}:{_original.LlmPort}/v1/models";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync(url).ConfigureAwait(true);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void TryOpenFolder(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
