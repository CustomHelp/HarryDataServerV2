@echo off
REM ==========================================================================
REM  deploy.cmd  --  HarryDataServer V2 production deploy (RUN ONLY IN THE STOP WINDOW)
REM
REM  Copies the Release build of the server + all 6 companions from the repo
REM  into  F:\003_Deploy\HarryDataServer\App\<Project>\ , keeping the previous
REM  deploy in App_prev\ for rollback. Writes a version.txt (date + git hash).
REM
REM  SAFETY: refuses to run while ANY Harry exe is still running (the plant must
REM  be stopped first). Never touches D:\ (that drive is the DVD). Read-only to
REM  F:\002_Configs (the live Harry.ini is NOT copied or changed here).
REM ==========================================================================
setlocal EnableExtensions EnableDelayedExpansion

REM --- Paths (repo root is the parent of this tools\ folder) ----------------
set "REPO=%~dp0.."
set "TFM=net8.0-windows"
set "CONFIG=Release"
set "DEPLOY=F:\003_Deploy\HarryDataServer"
set "APP=%DEPLOY%\App"
set "PREV=%DEPLOY%\App_prev"

set "PROJECTS=HarryDataServer HarryAnalysis HarryGraph HarryCounter HarryLimitSample HarryCollageCreator HarryPareto"

echo(
echo === HarryDataServer deploy ===
echo   Repo   : %REPO%
echo   Target : %APP%
echo(

REM --- 1. Refuse to run if anything is still running ------------------------
echo [1/5] Checking that no Harry process is running...
set "RUNNING="
for %%P in (%PROJECTS%) do (
    tasklist /FI "IMAGENAME eq %%P.exe" 2>nul | find /I "%%P.exe" >nul && (
        echo    STILL RUNNING: %%P.exe
        set "RUNNING=1"
    )
)
if defined RUNNING (
    echo(
    echo ABORT: stop the server and all companions first ^(window checklist step 1^).
    exit /b 2
)
echo    OK - nothing running.

REM --- 2. Verify the Release build exists for every project -----------------
echo [2/5] Verifying Release build output...
set "MISSING="
for %%P in (%PROJECTS%) do (
    if not exist "%REPO%\%%P\bin\%CONFIG%\%TFM%\%%P.exe" (
        echo    MISSING: %REPO%\%%P\bin\%CONFIG%\%TFM%\%%P.exe
        set "MISSING=1"
    )
)
if defined MISSING (
    echo(
    echo ABORT: build the solution in Release first ^(dotnet build -c Release^).
    exit /b 3
)
echo    OK - all 7 build outputs present.

REM --- 3. Snapshot the current deploy into App_prev (rollback) --------------
echo [3/5] Snapshotting current App -^> App_prev ...
if not exist "%APP%"  mkdir "%APP%"
if not exist "%PREV%" mkdir "%PREV%"
robocopy "%APP%" "%PREV%" /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:2 >nul
if errorlevel 8 (
    echo ABORT: could not snapshot App -^> App_prev.
    exit /b 4
)
echo    OK - previous deploy preserved in App_prev.

REM --- 4. Copy each project's Release output into App\<Project>\ ------------
echo [4/5] Copying build output...
for %%P in (%PROJECTS%) do (
    echo    - %%P
    robocopy "%REPO%\%%P\bin\%CONFIG%\%TFM%" "%APP%\%%P" /MIR /XD Logs Capture /NFL /NDL /NJH /NJS /NP /R:2 /W:2 >nul
    if errorlevel 8 (
        echo ABORT: robocopy failed for %%P.
        exit /b 5
    )
)
echo    OK - all projects copied.

REM --- 5. version.txt (date + git hash) ------------------------------------
echo [5/5] Writing version.txt ...
set "GITHASH=unknown"
for /f "delims=" %%H in ('git -C "%REPO%" rev-parse --short HEAD 2^>nul') do set "GITHASH=%%H"
(
    echo HarryDataServer deploy
    echo Date : %DATE% %TIME%
    echo Git  : %GITHASH%
    echo From : %REPO%
) > "%APP%\version.txt"
type "%APP%\version.txt"

echo(
echo === DONE. Production layout is ready at %APP% ===
echo   Server : %APP%\HarryDataServer\HarryDataServer.exe
echo   Rollback: copy App_prev back over App ^(window checklist step 6^).
endlocal
exit /b 0
