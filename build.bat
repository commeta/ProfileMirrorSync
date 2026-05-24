@echo off
echo =============================================
echo  ProfileMirrorSync - Build Script
echo =============================================
echo.

where dotnet >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [ERROR] .NET SDK not found. Download from:
    echo         https://dotnet.microsoft.com/download/dotnet/9.0
    pause
    exit /b 1
)

echo [1/4] Restoring packages...
dotnet restore src\ProfileMirrorSync.csproj
if %ERRORLEVEL% neq 0 goto :error

echo [2/4] Building Release...
dotnet build src\ProfileMirrorSync.csproj -c Release --no-restore
if %ERRORLEVEL% neq 0 goto :error

echo [3/4] Running tests (tests\ProfileMirrorSync.Tests.csproj)...
if exist tests\ProfileMirrorSync.Tests.csproj (
    dotnet restore tests\ProfileMirrorSync.Tests.csproj
    if %ERRORLEVEL% neq 0 goto :error
    dotnet build tests\ProfileMirrorSync.Tests.csproj -c Release --no-restore
    if %ERRORLEVEL% neq 0 goto :error
    dotnet test tests\ProfileMirrorSync.Tests.csproj -c Release --no-build
    if %ERRORLEVEL% neq 0 goto :error
) else (
    echo [skip] Tests directory not present
)

echo [4/4] Publishing single-file win-x64...
dotnet publish src\ProfileMirrorSync.csproj -c Release -r win-x64 --self-contained true -o publish\
if %ERRORLEVEL% neq 0 goto :error

echo.
echo =============================================
echo  BUILD SUCCESSFUL
echo  Output: publish\ProfileMirrorSync.exe
echo =============================================
pause
exit /b 0

:error
echo.
echo [ERROR] Build failed. See output above.
pause
exit /b 1
