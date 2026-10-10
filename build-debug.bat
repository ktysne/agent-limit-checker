@echo off
setlocal
cd /d "%~dp0"

rem dotnet がユーザー単位で入っている環境では PATH に無いことがある。
where dotnet >nul 2>nul
if errorlevel 1 set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"

echo [agent-limit-checker] Starting Debug build...
dotnet build dotnet\AgentLimitChecker.App -c Debug
if errorlevel 1 goto :failed

echo.
echo [agent-limit-checker] Debug build completed.
echo   exe: dotnet\AgentLimitChecker.App\bin\Debug\net10.0-windows\AgentLimitChecker.exe
pause
exit /b 0

:failed
echo.
echo [agent-limit-checker] Build failed. Check the log above.
pause
exit /b 1
