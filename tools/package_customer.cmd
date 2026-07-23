@echo off
REM ==========================================================================
REM  package_customer.cmd  --  build customer ZIPs for the companion tools
REM
REM  FRAMEWORK-DEPENDENT (target PC needs the .NET 8 Desktop Runtime x64).
REM  Per companion: dotnet publish -c Release -r win-x64 (NOT self-contained),
REM  stage with a stripped customer Harry.ini + README.txt, then ZIP to
REM  F:\100_Installer\CompanionTools\<Tool>_<version>.zip .
REM
REM  Restore runs once up front and is errorlevel-checked. If it fails (e.g. the
REM  win-x64 build assets are missing from the offline NuGet cache) the script
REM  ABORTS with a clear message instead of hanging. The per-tool publishes then
REM  use --no-restore (no network). Framework-dependent needs only the bundled
REM  AppHost pack, so it does NOT download the big runtime pack.
REM
REM  Safe anytime: writes only to F:\100_Installer\... ; never touches App\, the
REM  live Harry.ini or the running exes.
REM ==========================================================================
setlocal EnableExtensions EnableDelayedExpansion

set "REPO=%~dp0.."
set "CUST=%~dp0customer"
set "OUT=F:\100_Installer\CompanionTools"
set "STAGE=%OUT%\_stage"

set "TOOLS_INI=HarryAnalysis HarryGraph HarryCounter HarryLimitSample HarryCollageCreator"
set "TOOLS_NOINI=HarryPareto"

for /f "delims=" %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set "TODAY=%%d"
set "GITHASH=nogit"
for /f "delims=" %%H in ('git -C "%REPO%" rev-parse --short HEAD 2^>nul') do set "GITHASH=%%H"
set "VER=%TODAY%_%GITHASH%"

echo(
echo === Packaging customer companion tools (framework-dependent, %VER%) ===
if not exist "%OUT%" mkdir "%OUT%"

echo [restore] dotnet restore -r win-x64 ...
dotnet restore "%REPO%\HarryDataServer.sln" -r win-x64 --nologo
if errorlevel 1 (
    echo(
    echo ABORT: 'dotnet restore -r win-x64' failed. Offline this usually means the
    echo win-x64 build assets are missing from the NuGet cache - run it once on a PC
    echo with internet, then re-run this script.
    exit /b 2
)
echo    OK - restore complete.

for %%T in (%TOOLS_INI% %TOOLS_NOINI%) do (
    echo(
    echo --- %%T ---
    set "TDIR=%STAGE%\%%T"
    if exist "!TDIR!" rmdir /S /Q "!TDIR!"

    dotnet publish "%REPO%\%%T\%%T.csproj" -c Release -r win-x64 --self-contained false --no-restore --nologo -o "!TDIR!"
    if errorlevel 1 (
        echo ABORT: publish failed for %%T.
        exit /b 3
    )

    copy /Y "%CUST%\README.txt" "!TDIR!\README.txt" >nul
    echo %TOOLS_INI% | find /I "%%T" >nul && copy /Y "%CUST%\Harry.customer.ini" "!TDIR!\Harry.ini" >nul

    set "ZIP=%OUT%\%%T_%VER%.zip"
    if exist "!ZIP!" del /Q "!ZIP!"
    powershell -NoProfile -Command "Compress-Archive -Path '!TDIR!\*' -DestinationPath '!ZIP!' -Force"
    if errorlevel 1 (
        echo ABORT: zip failed for %%T.
        exit /b 4
    )
    echo    -^> !ZIP!
)

echo(
echo === DONE. Customer ZIPs in %OUT% ===
echo   Target PC needs the .NET 8 Desktop Runtime (x64) - put its installer next to
echo   the ZIPs (see README.txt). Also hand out tools\customer\readonly_user.sql.
endlocal
exit /b 0
