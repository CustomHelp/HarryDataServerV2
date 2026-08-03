@echo off
REM ==========================================================================
REM  Install.cmd  --  install the HarryDataServer companion tool(s) on a
REM                   customer PC.
REM
REM  USAGE:   Install.cmd  [target-folder]
REM           default target: C:\HarryTools
REM
REM  Works on a PC that has ONLY a C: drive. No admin rights are needed for the
REM  tools themselves - if C:\ cannot be written the script falls back to
REM  %LOCALAPPDATA%\HarryTools automatically. Admin rights are only requested by
REM  Windows if the .NET 8 Desktop Runtime still has to be installed.
REM
REM  Per tool it does:
REM    1. check the .NET 8 Desktop Runtime (x64), install it from the bundled
REM       installer when missing
REM    2. copy the program folder to <target>\<Tool>
REM    3. put a Harry.ini next to the exe - an EXISTING Harry.ini is NEVER
REM       overwritten, so re-installing keeps your database settings
REM    4. create a desktop shortcut
REM
REM  Re-running the script upgrades an existing installation.
REM ==========================================================================
setlocal EnableExtensions EnableDelayedExpansion

set "SRC=%~dp0"
if "%SRC:~-1%"=="\" set "SRC=%SRC:~0,-1%"

set "ROOT=%~1"
if "%ROOT%"=="" set "ROOT=C:\HarryTools"

echo(
echo ==========================================================
echo   HarryDataServer companion tools - installation
echo ==========================================================
echo   Source: %SRC%
echo   Target: %ROOT%
echo(

REM --- target folder (with fallback to the per-user location) ----------------
call :EnsureRoot || goto :fail
echo   Installing to: %ROOT%
echo(

REM --- .NET 8 Desktop Runtime ------------------------------------------------
call :EnsureRuntime

REM --- install every tool found ---------------------------------------------
set "COUNT=0"
if exist "%SRC%\Tools\" (
    for /d %%T in ("%SRC%\Tools\*") do call :InstallTool "%%~fT" "%%~nxT"
) else (
    set "SELF="
    for %%F in ("%SRC%\Harry*.exe") do if not defined SELF set "SELF=%%~nF"
    if not defined SELF (
        echo ERROR: no Harry*.exe found in this folder - is the ZIP fully extracted?
        goto :fail
    )
    call :InstallTool "%SRC%" "!SELF!"
)

if "%COUNT%"=="0" (
    echo ERROR: nothing was installed.
    goto :fail
)

echo(
echo ==========================================================
echo   DONE - %COUNT% tool(s) installed to %ROOT%
echo ==========================================================
echo(
echo   NEXT STEP - set the database connection ONCE per tool:
echo     open  %ROOT%\^<Tool^>\Harry.ini  in Notepad and fill in
echo        Server        = host name or IP of the MySQL server
echo        GetPassword   = the read-only password your admin set
echo   (or start the tool and use "Change config path..." in the top bar to
echo    point it at a Harry.ini you keep somewhere else)
echo(
echo   HarryPareto has no Harry.ini - it asks for the connection in its own
echo   dialog on first start.
echo(
pause
endlocal
exit /b 0

REM ==========================================================================
:EnsureRoot
if not exist "%ROOT%\" mkdir "%ROOT%" 2>nul
if not exist "%ROOT%\" (
    echo   NOTE: "%ROOT%" could not be created - no permission there?
    set "ROOT=%LOCALAPPDATA%\HarryTools"
    echo         Falling back to "!ROOT!"
    if not exist "!ROOT!\" mkdir "!ROOT!" 2>nul
)
if not exist "%ROOT%\" (
    echo ERROR: could not create a target folder.
    exit /b 1
)
exit /b 0

REM ==========================================================================
:EnsureRuntime
set "RT_OK="
for /d %%V in ("%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*") do set "RT_OK=1"
if defined RT_OK (
    echo   .NET 8 Desktop Runtime: already installed.
    echo(
    exit /b 0
)

set "RT_EXE="
for %%F in ("%SRC%\Runtime\windowsdesktop-runtime-8*win-x64.exe" "%SRC%\windowsdesktop-runtime-8*win-x64.exe" "%SRC%\..\windowsdesktop-runtime-8*win-x64.exe") do (
    if not defined RT_EXE if exist "%%~F" set "RT_EXE=%%~fF"
)

if not defined RT_EXE (
    echo   WARNING: the .NET 8 Desktop Runtime (x64) is NOT installed and its
    echo            installer was not found next to this script. The tools are
    echo            installed but will not start until you install
    echo            "windowsdesktop-runtime-8.x.x-win-x64.exe" - from the same
    echo            place you got this package, or from dotnet.microsoft.com
    echo(
    exit /b 0
)

echo   .NET 8 Desktop Runtime: missing - installing now
echo     %RT_EXE%
echo     (Windows may ask for administrator rights)
"%RT_EXE%" /install /passive /norestart
if errorlevel 1 (
    echo   WARNING: the runtime installer returned error code !errorlevel!.
    echo            Please run it manually as administrator, then start the tool.
) else (
    echo   .NET 8 Desktop Runtime: installed.
)
echo(
exit /b 0

REM ==========================================================================
REM  :InstallTool  <source-folder>  <tool-name>
:InstallTool
set "TSRC=%~1"
set "TNAME=%~2"
set "TDST=%ROOT%\%TNAME%"

echo --- %TNAME% ---
if not exist "%TSRC%\%TNAME%.exe" (
    echo   SKIPPED: %TNAME%.exe not found in "%TSRC%".
    echo(
    exit /b 0
)

if not exist "%TDST%\" mkdir "%TDST%" 2>nul
if not exist "%TDST%\" (
    echo   ERROR: could not create "%TDST%" - skipped.
    echo(
    exit /b 0
)

REM Copy the program files. Harry.ini is handled separately so an already
REM configured one survives an upgrade. Install.cmd itself is not copied.
robocopy "%TSRC%" "%TDST%" /E /NFL /NDL /NJH /NJS /NP /XF Harry.ini Install.cmd >nul
if errorlevel 8 (
    echo   ERROR: copying failed - skipped.
    echo(
    exit /b 0
)

if exist "%TDST%\Harry.ini" (
    echo   Harry.ini: kept the existing one - your settings are preserved.
) else (
    if exist "%TSRC%\Harry.ini" (
        copy /Y "%TSRC%\Harry.ini" "%TDST%\Harry.ini" >nul
        echo   Harry.ini: template copied - EDIT Server / GetPassword in it.
    ) else (
        echo   Harry.ini: not used by this tool - it has its own connection dialog.
    )
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
 "$d=[Environment]::GetFolderPath('Desktop'); $s=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $d '%TNAME%.lnk')); $s.TargetPath='%TDST%\%TNAME%.exe'; $s.WorkingDirectory='%TDST%'; $s.Description='HarryDataServer companion tool %TNAME%'; $s.Save()" >nul 2>&1
if errorlevel 1 (
    echo   Desktop shortcut: could not be created - start the exe from %TDST%.
) else (
    echo   Desktop shortcut: created.
)
echo   Installed to %TDST%
echo(

set /a COUNT+=1
exit /b 0

REM ==========================================================================
:fail
echo(
echo INSTALLATION ABORTED.
echo(
pause
endlocal
exit /b 1
