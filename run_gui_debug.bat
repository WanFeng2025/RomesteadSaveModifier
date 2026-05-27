@echo off
setlocal
cd /d "%~dp0"
set "ROMESTEAD_SAVE_INSPECTOR_ROOT=%~dp0"
echo Starting Romestead Save Inspector WinUI 3 in debug/source mode...
where dotnet >nul 2>nul
if errorlevel 1 (
  echo dotnet was not found. Install .NET 8 SDK first.
  pause
  exit /b 1
)
dotnet run --project "%~dp0RomesteadSaveInspector.WinUI\RomesteadSaveInspector.WinUI.csproj" -c Debug -r win-x64
if errorlevel 1 (
  echo.
  echo Application exited with an error. Please check logs\latest.log.
  pause
)
