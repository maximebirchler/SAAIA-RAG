param(
  [string]$RepoRoot = (Get-Location).Path
)

$mw = Join-Path $RepoRoot 'client\SAAIA.Client.WinUI\MainWindow.xaml.cs'
if (!(Test-Path $mw)) { throw "MainWindow.xaml.cs not found: $mw" }

$raw = Get-Content $mw -Raw
$backup = "$mw.bak_" + (Get-Date -Format 'yyyyMMdd_HHmmss')
Copy-Item $mw $backup -Force | Out-Null

$pattern = [regex]::Escape(@"
            // Avoid re-running every startup when provisioning didn't change.
            if (!force && !string.IsNullOrWhiteSpace(_appSettings.ProvisioningHash) &&
                string.Equals(_appSettings.ProvisioningHash, _appSettings.LlmAutoInstallAttemptedHash, StringComparison.OrdinalIgnoreCase))
            {
                Status("Assistant IA : réparation requise (Paramètres → Installer / réparer). ");
                return;
            }
"@)

$replacement = @"
            // If we already attempted bootstrap for this provisioning, do NOT re-download.
            // But we still try to start the embedded process from settings (common case: app restart).
            if (!force && !string.IsNullOrWhiteSpace(_appSettings.ProvisioningHash) &&
                string.Equals(_appSettings.ProvisioningHash, _appSettings.LlmAutoInstallAttemptedHash, StringComparison.OrdinalIgnoreCase))
            {
                try { await EnsureLocalLlmStartedFromSettingsAsync(CancellationToken.None); } catch { }

                var probe2 = await LlmEndpointProbe.GetModelsStatusAsync(
                    _appSettings.LlmBaseUrl,
                    TimeSpan.FromSeconds(6),
                    CancellationToken.None);

                if (probe2.Status == LlmModelsStatus.Ok) return;
                if (probe2.Status == LlmModelsStatus.Loading)
                {
                    Status("Assistant IA : chargement du modèle en cours…");
                    return;
                }

                Status("Assistant IA : réparation requise (Paramètres → Installer / réparer). ");
                return;
            }
"@

if ($raw -notmatch $pattern) {
  throw "Pattern not found in MainWindow.xaml.cs. Backup created at $backup. Apply manually."
}

$raw2 = [regex]::Replace($raw, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement }, 1)
Set-Content -Path $mw -Value $raw2 -Encoding UTF8

Write-Host "OK - MainWindow autostart fix applied. Backup: $backup" -ForegroundColor Green
