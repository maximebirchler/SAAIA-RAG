@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer-SAAIA.ps1"
if errorlevel 1 (
  echo.
  echo Installation interrompue. Aucun ancien profil SAAIA n'a ete remplace.
  pause
  exit /b 1
)
echo.
echo Installation terminee. Ouvrez le raccourci SAAIA - Presentation.
pause
