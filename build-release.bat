@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"

:askversion
set VERSION=
set /p VERSION=Enter version to publish as (e.g. 4.0.0, empty = keep csproj version):
if not defined VERSION goto :versiondone
echo(!VERSION!| findstr /r /x "[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*" >nul
if errorlevel 1 (
    echo [agent-limit-checker] Invalid version "!VERSION!". Use digits like 4.0.0.
    goto :askversion
)
:versiondone

set VEROPT=
if defined VERSION set VEROPT=-p:Version=%VERSION%

if defined VERSION (
    echo [agent-limit-checker] Publishing Release as version %VERSION%...
) else (
    echo [agent-limit-checker] Publishing Release with the csproj version...
)

dotnet publish dotnet\AgentLimitChecker.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded %VEROPT% -o dist\csharp
if errorlevel 1 goto :failed

echo.
echo [agent-limit-checker] Release publish completed.
echo   exe: dist\csharp\AgentLimitChecker.exe
pause
exit /b 0

:failed
echo.
echo [agent-limit-checker] Release publish failed. Check the log above.
pause
exit /b 1
