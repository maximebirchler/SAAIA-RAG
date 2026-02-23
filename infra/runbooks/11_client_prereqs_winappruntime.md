# Prérequis client WinUI : Windows App Runtime (WinAppSDK)

## Symptômes typiques si manquant
- Crash immédiat au lancement (aucune fenêtre)
- `COMException 0x80040154 (REGDB_E_CLASSNOTREG) Class not registered`
- ou process abort/fail-fast (`0xc0000602`)

## Cause
Les applications WinUI 3 basées sur **Windows App SDK** nécessitent la présence du **Microsoft Windows App Runtime** sur la machine (au moins en mode dev/unpackaged).

## Vérifier si le runtime est présent
PowerShell :

```powershell
Get-AppxPackage -Name "Microsoft.WindowsAppRuntime*" | Select Name, Version
```

Ou via le script :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\client\scripts\check-winappruntime.ps1
```

## Installer (recommandé)
### Option A : winget (si Internet autorisé)

```powershell
winget search "Windows App Runtime"
winget install Microsoft.WindowsAppRuntime.1.8 --accept-source-agreements --accept-package-agreements
```

### Option B : offline
Télécharger l’installateur / MSIX bundle officiel Windows App Runtime (x64) depuis Microsoft, puis l’installer sur la machine.

## Test
Après installation, relancer :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\client\scripts\run-client-x64.ps1
```
