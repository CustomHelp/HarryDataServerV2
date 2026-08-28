@echo off
REM ==========================================================================
REM  package_customer.cmd  --  build customer packages for the companion tools
REM
REM  FRAMEWORK-DEPENDENT (target PC needs the .NET 8 Desktop Runtime x64).
REM  Per companion: dotnet publish -c Release -r win-x64 (NOT self-contained),
REM  stage with a stripped customer Harry.ini + README.txt + Install.cmd.
REM
REM  OUTPUT in F:\100_Installer\CompanionTools :
REM    HarryCompanionTools_<version>.zip   <- HAND THIS TO THE CUSTOMER.
REM        All tools + the .NET 8 Desktop Runtime installer + Install.cmd.
REM        Extract anywhere, run Install.cmd -> installs to C:\HarryTools
REM        (falls back to %LOCALAPPDATA%\HarryTools), desktop shortcuts, and
REM        installs the runtime if it is missing. No D: / F: drive required.
REM    <Tool>_<version>.zip                <- single tool, same Install.cmd.
REM    windowsdesktop-runtime-...exe       <- copied next to the ZIPs.
REM    README.txt / readonly_user.sql
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
set "BUNDLE=%STAGE%\_bundle"

REM Where to look for the .NET 8 Desktop Runtime installer to bundle (first hit wins).
set "RT_SEARCH=F:\100_Installer\Sonstiges\windowsdesktop-runtime-8*win-x64.exe F:\100_Installer\windowsdesktop-runtime-8*win-x64.exe"

set "TOOLS_INI=HarryAnalysis HarryGraph HarryCounter HarryLimitSample HarryCollageCreator"
set "TOOLS_NOINI=HarryPareto"

for /f "delims=" %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set "TODAY=%%d"
set "GITHASH=nogit"
for /f "delims=" %%H in ('git -C "%REPO%" rev-parse --short HEAD 2^>nul') do set "GITHASH=%%H"
set "VER=%TODAY%_%GITHASH%"

echo(
echo === Packaging customer companion tools (framework-dependent, %VER%) ===
if not exist "%OUT%" mkdir "%OUT%"
if exist "%BUNDLE%" rmdir /S /Q "%BUNDLE%"
mkdir "%BUNDLE%\Tools"

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
    copy /Y "%CUST%\Install.cmd" "!TDIR!\Install.cmd" >nul
    REM Membership test in pure cmd - deliberately NOT "echo %TOOLS_INI% | find /I".
    REM "find" was resolved from PATH, so running this script from a shell that puts
    REM Git Bash's /usr/bin ahead of System32 picked up the Unix find, which failed;
    REM the && then silently skipped the copy and the package shipped WITHOUT the
    REM customer Harry.ini. The copy is now verified and aborts loudly instead.
    set "NEEDS_INI="
    if not "!TOOLS_INI:%%T=!" == "%TOOLS_INI%" set "NEEDS_INI=1"
    if defined NEEDS_INI (
        copy /Y "%CUST%\Harry.customer.ini" "!TDIR!\Harry.ini" >nul
        if not exist "!TDIR!\Harry.ini" (
            echo ABORT: could not stage Harry.ini for %%T.
            exit /b 6
        )
    )

    REM the all-in-one bundle gets the same folder, but WITHOUT the per-tool
    REM Install.cmd/README (they live once at the bundle root)
    robocopy "!TDIR!" "%BUNDLE%\Tools\%%T" /E /NFL /NDL /NJH /NJS /NP /XF Install.cmd README.txt >nul
    if errorlevel 8 (
        echo ABORT: staging the bundle copy failed for %%T.
        exit /b 5
    )

    set "ZIP=%OUT%\%%T_%VER%.zip"
    if exist "!ZIP!" del /Q "!ZIP!"
    powershell -NoProfile -Command "Compress-Archive -Path '!TDIR!\*' -DestinationPath '!ZIP!' -Force"
    if errorlevel 1 (
        echo ABORT: zip failed for %%T.
        exit /b 4
    )
    echo    -^> !ZIP!
)

REM --- the .NET runtime installer (bundled + placed next to the ZIPs) --------
echo(
echo --- .NET 8 Desktop Runtime installer ---
set "RT_EXE="
for %%P in (%RT_SEARCH%) do (
    for %%F in ("%%~P") do if not defined RT_EXE if exist "%%~fF" set "RT_EXE=%%~fF"
)
if defined RT_EXE (
    mkdir "%BUNDLE%\Runtime" 2>nul
    copy /Y "!RT_EXE!" "%BUNDLE%\Runtime\" >nul
    copy /Y "!RT_EXE!" "%OUT%\" >nul
    echo    bundled: !RT_EXE!
) else (
    echo    WARNING: no windowsdesktop-runtime-8*win-x64.exe found in
    echo             F:\100_Installer\Sonstiges - the bundle will NOT contain the
    echo             runtime. Put the installer next to the ZIPs by hand.
)

REM --- the all-in-one customer bundle ---------------------------------------
echo(
echo --- all-in-one bundle ---
copy /Y "%CUST%\Install.cmd" "%BUNDLE%\Install.cmd" >nul
copy /Y "%CUST%\README.txt" "%BUNDLE%\README.txt" >nul
copy /Y "%CUST%\readonly_user.sql" "%BUNDLE%\readonly_user.sql" >nul

set "ALLZIP=%OUT%\HarryCompanionTools_%VER%.zip"
if exist "%ALLZIP%" del /Q "%ALLZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%BUNDLE%\*' -DestinationPath '%ALLZIP%' -Force"
if errorlevel 1 (
    echo ABORT: zip failed for the all-in-one bundle.
    exit /b 6
)
echo    -^> %ALLZIP%

copy /Y "%CUST%\README.txt" "%OUT%\README.txt" >nul
copy /Y "%CUST%\readonly_user.sql" "%OUT%\readonly_user.sql" >nul

echo(
echo === DONE. Customer packages in %OUT% ===
echo   Hand out: HarryCompanionTools_%VER%.zip  (extract - run Install.cmd)
echo   It contains all tools, the .NET 8 Desktop Runtime installer and the
echo   read-only Harry.ini template. Installs to C:\HarryTools - no D:/F: needed.
endlocal
exit /b 0
