using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

internal sealed partial class UserSettingsDialog
{
    private async Task ExportSupportBundleAsync()
    {
        if (_busy)
            return;

        SetBusy(true, T("settings.status.exporting"));
        try
        {
            var zipPath = await SupportBundleBuilder.BuildAsync(_original).ConfigureAwait(true);
            _assistantStatus.Text = T("settings.status.exported") + Environment.NewLine + zipPath;
            TryOpenFolder(Path.GetDirectoryName(zipPath));
        }
        catch (Exception ex)
        {
            _assistantStatus.Text = T("settings.status.export_failed") + Environment.NewLine + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunRepairAsync()
    {
        if (_repairAssistantAsync is not null)
        {
            if (_busy)
                return;

            SetBusy(true, T("settings.status.repair.running"));
            _assistantRepairBtn.IsEnabled = false;
            _assistantProgress.Visibility = Visibility.Visible;
            _assistantProgress.IsIndeterminate = true;

            try
            {
                await _repairAssistantAsync().ConfigureAwait(true);
                if (await WaitForModelsReadyAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(true))
                    _assistantStatus.Text = T("settings.status.repair.ready");
                else
                    _assistantStatus.Text = T("settings.status.repair.not_ready");
            }
            catch (Exception ex)
            {
                _assistantStatus.Text = T("settings.status.failed_prefix") + ex.Message;
            }
            finally
            {
                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantRepairBtn.IsEnabled = true;
                SetBusy(false);
            }

            return;
        }

        await RepairAssistantAsync().ConfigureAwait(true);
    }

    private async Task RepairAssistantAsync()
    {
        if (_busy)
            return;

        SetBusy(true, T("settings.status.repair.running"));
        _assistantRepairBtn.IsEnabled = false;
        _assistantProgress.Visibility = Visibility.Visible;
        _assistantProgress.IsIndeterminate = true;
        _assistantStatus.Text = T("settings.status.repair.checking");

        try
        {
            if (await IsLlmReadyAsync().ConfigureAwait(true))
            {
                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantStatus.Text = T("settings.status.repair.ready");
                return;
            }

            var (scriptPath, _) = TryGetInstallScriptFromProvisioning();
            if (!string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath))
            {
                var ok = await RunInstallScriptElevatedAndWaitAsync(scriptPath).ConfigureAwait(true);
                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantStatus.Text = ok ? T("settings.status.repair.ready") : T("settings.status.repair.cancelled");
                return;
            }

            if (Provisioning.TryGetDownloadAssets(out var assets, out _) && assets.Count > 0)
            {
                _assistantStatus.Text = T("settings.status.repair.downloading");
                _assistantProgress.IsIndeterminate = false;
                _assistantProgress.Value = 0;

                var prog = new Progress<DownloadManager.ProgressInfo>(p =>
                {
                    if (p.TotalBytes is long total && total > 0)
                    {
                        var pct = (double)p.BytesDownloaded / total * 100.0;
                        _assistantProgress.Value = Math.Clamp(pct, 0, 100);
                    }
                    else
                    {
                        _assistantProgress.IsIndeterminate = true;
                    }

                    _assistantStatus.Text = $"{T("settings.status.repair.downloading")} ({p.AssetId})";
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await _downloads.EnsureAssetsAsync(assets, prog, cts.Token).ConfigureAwait(true);

                _assistantProgress.Visibility = Visibility.Collapsed;
                _assistantStatus.Text = T("settings.status.repair.download_done");
                return;
            }

            _assistantProgress.Visibility = Visibility.Collapsed;
            _assistantStatus.Text = T("settings.status.repair.no_auto");
        }
        catch (Exception ex)
        {
            _assistantProgress.Visibility = Visibility.Collapsed;
            _assistantStatus.Text = T("settings.status.failed_prefix") + ex.Message;
        }
        finally
        {
            _assistantRepairBtn.IsEnabled = true;
            SetBusy(false);
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
        _assistantStatus.Text = T("settings.status.repair.waiting");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? string.Empty
            };

            Process.Start(psi);
        }
        catch (Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            _assistantStatus.Text = T("settings.status.repair.cancelled");
            return false;
        }
        catch (Exception ex)
        {
            _assistantStatus.Text = T("settings.status.repair.launch_failed") + Environment.NewLine + ex.Message;
            return false;
        }

        _assistantProgress.IsIndeterminate = true;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsLlmReadyAsync().ConfigureAwait(true))
                return true;

            await Task.Delay(1500).ConfigureAwait(true);
        }

        _assistantStatus.Text = T("settings.status.repair.timeout");
        return false;
    }

    private async Task<bool> WaitForModelsReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        _assistantProgress.Visibility = Visibility.Visible;
        _assistantProgress.IsIndeterminate = true;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var probe = await LlmEndpointProbe.GetModelsStatusAsync(
                _original.LlmBaseUrl,
                TimeSpan.FromSeconds(8),
                CancellationToken.None).ConfigureAwait(true);

            if (probe.Status == LlmModelsStatus.Ok)
                return true;

            _assistantStatus.Text = probe.Status == LlmModelsStatus.Loading
                ? T("settings.status.repair.loading")
                : T("settings.status.repair.starting");

            await Task.Delay(1000).ConfigureAwait(true);
        }

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

    private static void TryOpenFolder(string? folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder))
                return;

            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}
