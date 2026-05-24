@echo off
setlocal EnableExtensions EnableDelayedExpansion

echo =============================================
echo  ProfileMirrorSync - Uninstall Script
echo =============================================
echo.
echo This will remove:
echo   - the scheduled task "ProfileMirrorSync"
echo   - the running ProfileMirrorSync.exe process (if any)
echo   - the program binaries in %ProgramFiles%\ProfileMirrorSync
echo   - the public desktop shortcut
echo.
echo Per-user settings, sync state AND logs in each user's
echo   %%LocalAppData%%\ProfileMirrorSync are NOT removed (that is each
echo   user's own data). This script offers to remove only the CURRENT
echo   user's data at the end.
echo.

REM Admin check: schtasks /Delete (all-users task) and rmdir on %ProgramFiles%
REM need administrator privileges.
net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Run this script as Administrator.
    pause
    exit /b 1
)

set "APP=ProfileMirrorSync"
set "DSTROOT=%ProgramFiles%\ProfileMirrorSync"
set "TASK=%APP%"
set "LNK=%PUBLIC%\Desktop\ProfileMirrorSync.lnk"
set "USERDATA=%LocalAppData%\ProfileMirrorSync"

echo [1/4] Stopping scheduled task and any running process...
schtasks /End    /TN "%TASK%" >nul 2>&1
schtasks /Delete /TN "%TASK%" /F >nul 2>&1
tasklist /FI "IMAGENAME eq ProfileMirrorSync.exe" 2>nul | find /I "ProfileMirrorSync.exe" >nul
if not errorlevel 1 (
    taskkill /IM ProfileMirrorSync.exe /F /T >nul 2>&1
    timeout /t 2 /nobreak >nul
)

echo [2/4] Removing the public desktop shortcut...
if exist "%LNK%" del /F /Q "%LNK%"

echo [3/4] Removing program binaries...
if exist "%DSTROOT%" rd /S /Q "%DSTROOT%"

echo [4/4] Current user's settings and sync state...
if exist "%USERDATA%" (
    set /p REPLY="Remove THIS user's settings/state in %USERDATA%? [y/N] "
    if /I "!REPLY!"=="y" (
        rd /S /Q "%USERDATA%"
        echo       Removed.
    ) else (
        echo       Kept. Other users' data under their own %%LocalAppData%% is untouched.
    )
) else (
    echo       Nothing to remove for the current user.
)

echo.
echo Done.
echo Note: files already copied to the backup destination are NOT touched.
echo       To fully purge a multi-user PC, remove
echo       %%LocalAppData%%\ProfileMirrorSync for each user profile.
echo.
endlocal
pause
