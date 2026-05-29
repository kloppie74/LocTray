@echo off
cd /d "%~dp0"

echo ===========================================
echo   LocTray - Building single-file EXE
echo ===========================================
echo.

REM Stop draaiende LocTray.exe (anders is het bestand vergrendeld)
echo Eventuele draaiende LocTray.exe afsluiten...
taskkill /F /IM LocTray.exe >nul 2>nul
if %errorlevel% equ 0 (
    echo   ^> Oude instance gesloten.
    REM Geef Windows even tijd om de file lock vrij te geven
    timeout /t 1 /nobreak >nul
) else (
    echo   ^> Geen draaiende instance gevonden.
)
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [FOUT] De .NET SDK is niet geinstalleerd.
    echo.
    echo Installeer .NET 8 SDK via:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -p:PublishReadyToRun=false

if errorlevel 1 (
    echo.
    echo ===========================================
    echo   BUILD MISLUKT
    echo ===========================================
    pause
    exit /b 1
)

echo.
echo ===========================================
echo   KLAAR! LocTray.exe is gebouwd.
echo ===========================================
echo.
echo Locatie:
echo   bin\Release\net8.0-windows\win-x64\publish\LocTray.exe
echo.

start "" explorer "bin\Release\net8.0-windows\win-x64\publish"
