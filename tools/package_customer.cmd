@echo off
REM ==========================================================================
REM  package_customer.cmd  --  build customer ZIPs for the companion tools
REM
REM  For each companion: dotnet publish (self-contained win-x64, includes the
REM  .NET runtime), stage with a STRIPPED customer Harry.ini (read-only DB
REM  placeholder, no F:, no write user) + README.txt, then ZIP to
REM  F:\100_Installer\CompanionTools\<Tool>_<version>.zip .
REM
REM  Safe to run anytime — it only writes to its own staging/output folders and
REM  never touches production (App\), the live Harry.ini or the running exes.
REM ==========================================================================
setlocal EnableExtensions EnableDelayedExpansion

set "REPO=%~dp0.."
set "CUST=%~dp0customer"
set "OUT=F:\100_Installer\CompanionTools"
set "STAGE=%OUT%\_stage"

REM 5 config-driven tools get the stripped Harry.ini; HarryPareto uses its own
REM connection dialog, so it ships without a Harry.ini.
set "TOOLS_INI=HarryAnalysis HarryGraph HarryCounter HarryLimitSample HarryCollageCreator"
set "TOOLS_NOINI=HarryPareto"

REM --- version token: yyyyMMdd_<gitshort> ----------------------------------
for /f "tokens=1-3 delims=/.- " %%a in ("%DATE%") do set "TODAY=%%c%%b%%a"
set "GITHASH=nogit"
for /f "delims=" %%H in ('git -C "%REPO%" rev-parse --short HEAD 2^>nul') do set "GITHASH=%%H"
set "VER=%TODAY%_%GITHASH%"

echo(
echo === Packaging customer companion tools (%VER%) ===
if not exist "%OUT%" mkdir "%OUT%"

for %%T in (%TOOLS_INI% %TOOLS_NOINI%) do (
    echo(
    echo --- %%T ---
    set "TDIR=%STAGE%\%%T"
    if exist "!TDIR!" rmdir /S /Q "!TDIR!"

    dotnet publish "%REPO%\%%T\%%T.csproj" -c Release -r win-x64 --self-contained true ^
        -p:PublishSingleFile=false --nologo -o "!TDIR!"
    if errorlevel 1 (
        echo ABORT: publish failed for %%T.
        exit /b 2
    )

    REM README for every tool
    copy /Y "%CUST%\README.txt" "!TDIR!\README.txt" >nul

    REM stripped Harry.ini only for the config-driven tools
    echo %TOOLS_INI% | find /I "%%T" >nul && copy /Y "%CUST%\Harry.customer.ini" "!TDIR!\Harry.ini" >nul

    set "ZIP=%OUT%\%%T_%VER%.zip"
    if exist "!ZIP!" del /Q "!ZIP!"
    powershell -NoProfile -Command "Compress-Archive -Path '!TDIR!\*' -DestinationPath '!ZIP!' -Force"
    if errorlevel 1 (
        echo ABORT: zip failed for %%T.
        exit /b 3
    )
    echo    -^> !ZIP!
)

echo(
echo === DONE. Customer ZIPs in %OUT% ===
echo   Remember: hand out readonly_user.sql + the MySQL remote-access steps
echo   (tools\customer\readonly_user.sql) so the customer's admin can enable access.
endlocal
exit /b 0
