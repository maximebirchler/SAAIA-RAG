param(
    [string]$RepoRoot = "C:\Users\MBirchler\Desktop\ecom\SAAIA\GIT\RAG",
    [string]$ComposeFile = ".\infra\docker-compose.prod.yml",
    [string]$EnvFile = ".\infra\.env",
    [string]$InstallScript = ".\infra\scripts\prod\install.ps1",
    [string]$InstallRoot = "C:\SAAIA",
    [string]$PostgresService = "postgres",
    [string]$PostgresDatabase = "saaia",
    [string]$PostgresUser = "saaia-admin",
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Assert-PathExists([string]$PathToCheck, [string]$Label) {
    if (-not (Test-Path $PathToCheck)) {
        throw "$Label introuvable : $PathToCheck"
    }
}

function Invoke-Compose {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Args
    )

    & docker compose -f $ComposeFile --env-file $EnvFile @Args
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose a échoué : $($Args -join ' ')"
    }
}

function Invoke-PsqlCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Sql
    )

    Invoke-Compose -Args @(
        'exec', '-T', $PostgresService,
        'psql', '-v', 'ON_ERROR_STOP=1',
        '-U', $PostgresUser,
        '-d', $PostgresDatabase,
        '-c', $Sql
    )
}

Set-Location $RepoRoot
Assert-PathExists $ComposeFile 'Compose file'
Assert-PathExists $EnvFile 'Env file'
Assert-PathExists $InstallScript 'Install script'

$qdrantPath = Join-Path $InstallRoot 'data\qdrant'

Write-Step 'Arrêt de la stack'
Invoke-Compose -Args @('down', '--remove-orphans')

Write-Step 'Nettoyage du stockage Qdrant (les clés API Postgres sont conservées)'
if (Test-Path $qdrantPath) {
    Remove-Item $qdrantPath -Recurse -Force -ErrorAction Stop
}
New-Item -ItemType Directory -Force -Path $qdrantPath | Out-Null

Write-Step 'Démarrage de Postgres uniquement'
Invoke-Compose -Args @('up', '-d', $PostgresService)

Write-Step 'Attente de disponibilité Postgres'
$maxAttempts = 30
$delaySeconds = 2
$postgresReady = $false
for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    & docker compose -f $ComposeFile --env-file $EnvFile exec -T $PostgresService pg_isready -U $PostgresUser -d $PostgresDatabase *> $null
    if ($LASTEXITCODE -eq 0) {
        $postgresReady = $true
        break
    }
    Start-Sleep -Seconds $delaySeconds
}

if (-not $postgresReady) {
    throw "Postgres n'est pas devenu prêt dans le délai imparti."
}

Write-Step 'Purge ciblée ingestion/documents/catalogue (sans toucher api_keys/licenses/seats)'
$sql = @'
DO $$
DECLARE
    target_tables text[] := ARRAY[
        'ingestion_jobs',
        'admin_jobs',
        'document_summaries',
        'documents',
        'documents_category_nodes',
        'documents_catalog_category_aliases',
        'documents_catalog_categories',
        'documents_catalog_summary'
    ];
    existing_tables text;
BEGIN
    SELECT string_agg(format('%I', table_name), ', ' ORDER BY table_name)
    INTO existing_tables
    FROM information_schema.tables
    WHERE table_schema = 'public'
      AND table_name = ANY(target_tables);

    IF existing_tables IS NOT NULL THEN
        EXECUTE 'TRUNCATE TABLE ' || existing_tables || ' RESTART IDENTITY CASCADE';
    END IF;
END $$;
'@
Invoke-PsqlCommand -Sql $sql

Write-Step 'Contrôle rapide des clés API conservées'
Invoke-PsqlCommand -Sql "SELECT api_key_id, label, is_admin, revoked_at FROM api_keys ORDER BY created_at NULLS FIRST, api_key_id;"

Write-Step 'Contrôle rapide des volumes remis à zéro'
Invoke-PsqlCommand -Sql @'
SELECT 'documents' AS table_name, COUNT(*) AS count FROM documents
UNION ALL
SELECT 'ingestion_jobs', COUNT(*) FROM ingestion_jobs
UNION ALL
SELECT 'admin_jobs', COUNT(*) FROM admin_jobs
UNION ALL
SELECT 'document_summaries', COUNT(*) FROM document_summaries
ORDER BY table_name;
'@

if (-not $SkipInstall) {
    Write-Step 'Redéploiement complet'
    & powershell -ExecutionPolicy Bypass -File $InstallScript
    if ($LASTEXITCODE -ne 0) {
        throw "install.ps1 a échoué."
    }
}

Write-Step 'Terminé'
Write-Host 'Reset ingestion terminé. Les clés API ont été conservées.' -ForegroundColor Green
Write-Host "Prochaine étape conseillée : relancer le client, puis vérifier qu'aucun job fantôme ne réapparaît." -ForegroundColor Green
