@echo off
setlocal EnableExtensions

echo =============================================
echo  ProfileMirrorSync - Install Script
echo =============================================
echo.

REM ---------------------------------------------------------------------------
REM Admin elevation check.
REM   - copying into %ProgramFiles%\ProfileMirrorSync (standard, all-users)
REM     needs admin;
REM   - schtasks /Create for an all-users ONLOGON trigger needs admin;
REM   - taskkill /F against a process in another user's session needs admin.
REM Fail fast with a clear message instead of half-installing.
REM ---------------------------------------------------------------------------
net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Run this script as Administrator.
    echo         Right-click install.bat and choose
    echo         "Run as administrator".
    pause
    exit /b 1
)

set "APP=ProfileMirrorSync"
set "DST=%ProgramFiles%\ProfileMirrorSync"
set "EXE=%DST%\ProfileMirrorSync.exe"
set "TASK=%APP%"
set "LNK=%PUBLIC%\Desktop\ProfileMirrorSync.lnk"

REM ---------------------------------------------------------------------------
REM Locate the build output produced by build.bat (step [4/4] "publish").
REM build.bat publishes to .\publish\ ; we also accept .\public\ in case the
REM folder was renamed.  The script lives in the repo root, so %~dp0 anchors
REM the search there.
REM ---------------------------------------------------------------------------
set "SRC="
if exist "%~dp0publish\ProfileMirrorSync.exe" set "SRC=%~dp0publish"
if not defined SRC if exist "%~dp0public\ProfileMirrorSync.exe" set "SRC=%~dp0public"

if not defined SRC (
    echo [ERROR] Build output not found.
    echo         Expected: "%~dp0publish\ProfileMirrorSync.exe"
    echo         Run build.bat first, then re-run install.bat from the
    echo         same folder.
    pause
    exit /b 1
)
echo Source: %SRC%
echo Target: %DST%
echo.

echo [1/5] Stopping the program if it is running...
tasklist /FI "IMAGENAME eq ProfileMirrorSync.exe" 2>nul | find /I "ProfileMirrorSync.exe" >nul
if not errorlevel 1 (
    taskkill /IM ProfileMirrorSync.exe /F /T >nul 2>&1
    timeout /t 2 /nobreak >nul
)

echo [2/5] Copying files...
if not exist "%DST%" mkdir "%DST%"
robocopy "%SRC%" "%DST%" /E /COPY:DAT /R:1 /W:1 >nul
set "RC=%errorlevel%"
REM robocopy exit codes < 8 are success (0-7 = copied / extra / mismatch, all OK).
if %RC% GEQ 8 (
    echo [ERROR] Copy failed. Robocopy exit code: %RC%
    pause
    exit /b %RC%
)

echo [3/5] Creating the public desktop shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ws = New-Object -ComObject WScript.Shell; $lnk = $ws.CreateShortcut($env:LNK); $lnk.TargetPath = $env:EXE; $lnk.WorkingDirectory = (Split-Path $env:EXE); $lnk.WindowStyle = 1; $lnk.IconLocation = $env:EXE + ',0'; $lnk.Save()"

echo [4/5] Registering the logon task (starts for every user at logon)...
REM PMS is a per-user tray app (app.manifest = asInvoker, no elevation).
REM /SC ONLOGON with no /RU fires for ANY user who logs on, so every user of
REM a shared PC gets their own tray instance with their own per-user settings
REM (%LocalAppData%).  Default run level (LIMITED) matches the manifest.
schtasks /Delete /TN "%TASK%" /F >nul 2>&1
schtasks /Create /TN "%TASK%" /TR "\"%EXE%\"" /SC ONLOGON /IT >nul
if errorlevel 1 (
    echo [WARN] Could not register the scheduled task. The program is
    echo        installed; you can start it manually from the shortcut.
)

echo [5/5] Starting the program...
start "" "%EXE%"

echo.
echo Done. Installed to:
echo   %DST%
echo The program runs in the tray. Per-user settings AND logs are stored in
echo   %%LocalAppData%%\ProfileMirrorSync (separate for each Windows user).
echo.
endlocal
pause
