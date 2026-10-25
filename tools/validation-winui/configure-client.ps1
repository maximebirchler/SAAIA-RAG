#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [int]$ModelPort = 18087, [string]$ModelId = 'saaia-winui-fixture')
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$owner = Get-Content -LiteralPath (Join-Path $output 'environment.json') -Raw | ConvertFrom-Json
if ($owner.schemaVersion -ne 'saaia-winui-local-runtime.v1' -or $owner.outputDirectory -ine $output) { throw 'Not an owned fixture.' }
$clientDirectory = [IO.Path]::GetFullPath((Join-Path $owner.clientDataRoot 'client'))
$expectedRoot = [IO.Path]::GetFullPath((Join-Path $output 'runtime/client-data'))
if ([IO.Path]::GetFullPath($owner.clientDataRoot) -ine $expectedRoot) { throw 'Client directory is not the owned isolated root.' }
[IO.Directory]::CreateDirectory($clientDirectory) | Out-Null
$settingsPath = Join-Path $clientDirectory 'settings.json'
$securePath = Join-Path $clientDirectory 'secure.json'
if ((Test-Path -LiteralPath $settingsPath) -or (Test-Path -LiteralPath $securePath)) { throw 'Existing isolated client data is preserved; configure a fresh root or use the client settings UI.' }
$private = Get-Content -LiteralPath (Join-Path $output 'runtime/fixture-secrets.json') -Raw | ConvertFrom-Json
$entropy = [Text.Encoding]::UTF8.GetBytes('SAAIA.Client.WinUI|CDC-v2.7')
$protectedKey = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($private.apiKey), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
$userId = [Guid]::NewGuid().ToString('D')
$secure = @{ UserId = $userId; ServerApiKeyProtected = [Convert]::ToBase64String($protectedKey); LegacyApiKeyPlain = $null; OpenAiApiKeyProtected = $null; RunPodApiKeyProtected = $null }
[IO.File]::WriteAllText($securePath, ($secure | ConvertTo-Json))
$settings = @{ BackendUrl = $owner.backendUrl; BackendUrlAlternates = ''; ShowAdvancedUi = $true; AutoConnect = $true; UiLanguage = 'fr'; UiTheme = 'dark';
    LlmMode = 'external'; UseLocalLlm = $true; ManageLocalLlmProcess = $false; AutoStartOnConnect = $false; EagerLoad = $false;
    LlamaExePath = ''; ModelPath = ''; Host = '127.0.0.1'; Port = $ModelPort; ModelId = $ModelId; ExtraArgs = '--ctx-size 8192';
    UbatchSize = 128; ThreadsBatch = 4; FlashAttn = $null; QualifiedProfile = $null; StartupTimeoutSeconds = 60;
    LlmTemperature = 0; LlmMaxOutputTokens = 800; RagQualityPreset = 'balanced'; LastSessionId = $null }
[IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 8))
@{ schemaVersion = 'saaia-winui-client-isolation.v1'; userId = $userId; clientDataRoot = $owner.clientDataRoot;
    backendUrl = $owner.backendUrl; localModelPort = $ModelPort; modelId = $ModelId; providerKeysPresent = $false;
    syntheticBackendKeyProtectedWithDpapi = $true; realUserConfigurationModified = $false; clientStarted = $false } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'client-isolation.json') -Encoding utf8
Write-Output "Isolated synthetic settings created under $($owner.clientDataRoot). No client started."
