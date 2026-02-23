# WinUI — Fix crash (fail-fast / REGDB_E_CLASSNOTREG)

## Symptom
- App compiles but exits immediately.
- Logs show COMException `REGDB_E_CLASSNOTREG` or fail-fast `0xc0000602`.

## Root causes (common)
- Mixing multiple initialization strategies (self-contained + bootstrap + deployment manager).
- Custom `StartUp.cs` (manual entry point) left in the project.

## Fix applied by this patch
- Revert to **framework-dependent unpackaged** with **bootstrap auto-initializer ON**.
- Disable DeploymentManager auto initializer.
- Remove `StartUp.cs` from the project.

## Commands

### Remove the manual entry point (IMPORTANT)
From repo root:

```powershell
git rm client/SAAIA.Client.WinUI/StartUp.cs
```

If you are not using git, simply delete:
`client/SAAIA.Client.WinUI/StartUp.cs`

### Clean + run
```powershell
dotnet clean .\client\SAAIA.Client.WinUI\SAAIA.Client.WinUI.csproj -p:Platform=x64
Remove-Item -Recurse -Force .\client\SAAIA.Client.WinUI\bin, .\client\SAAIA.Client.WinUI\obj -ErrorAction SilentlyContinue

powershell -NoProfile -ExecutionPolicy Bypass -File .\client\scripts\run-client-x64.ps1
```

If it still exits quickly, inspect:
`%LOCALAPPDATA%\SAAIA\logs\client_startup.log`
