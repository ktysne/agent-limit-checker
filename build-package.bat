@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
set "CONFIG=Release"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [agent-limit-checker] ERROR: dotnet was not found on PATH.
    goto :failed
)
where node >nul 2>nul
if errorlevel 1 (
    echo [agent-limit-checker] ERROR: Node.js was not found on PATH.
    goto :failed
)
node --use-system-ca -e 0 >nul 2>nul
if errorlevel 1 (
    echo [agent-limit-checker] ERROR: Node.js 22.15 or later is required.
    goto :failed
)
if not exist "node_modules\basic-ftp" (
    echo [agent-limit-checker] ERROR: basic-ftp is missing.
    echo Run "npm install" in the repository root first.
    goto :failed
)
where git >nul 2>nul
if errorlevel 1 (
    echo [agent-limit-checker] ERROR: git was not found on PATH.
    goto :failed
)
git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
    echo [agent-limit-checker] ERROR: this directory is not a git worktree.
    goto :failed
)

set "DIRTY="
for /f "delims=" %%L in ('git status --porcelain --untracked-files^=normal') do set "DIRTY=1"
if defined DIRTY (
    echo [agent-limit-checker] ERROR: commit or discard all worktree changes before packaging.
    git status --short
    goto :failed
)

set "BUILD_HASH="
for /f "delims=" %%H in ('git rev-parse HEAD') do set "BUILD_HASH=%%H"
if not defined BUILD_HASH (
    echo [agent-limit-checker] ERROR: could not determine the build commit.
    goto :failed
)

set "PUBLISHED="
for /f "delims=" %%P in ('node --use-system-ca tools\release-site.js published-version 2^>nul') do set "PUBLISHED=%%P"
if not defined PUBLISHED set "PUBLISHED=none or unavailable"
set "LASTTAG="
for /f "delims=" %%T in ('git tag --list v* --sort^=v:refname 2^>nul') do set "LASTTAG=%%T"
if not defined LASTTAG set "LASTTAG=none"
set "PROJECTVER="
for /f "tokens=2 delims=<>" %%V in ('findstr /c:"<Version " "dotnet\AgentLimitChecker.App\AgentLimitChecker.App.csproj') do set "PROJECTVER=%%V"
echo.
echo [agent-limit-checker] Published version: %PUBLISHED%
echo [agent-limit-checker] Latest local tag:  %LASTTAG%
echo [agent-limit-checker] Project version:   %PROJECTVER%

:askversion
set "VERSION="
set /p "VERSION=Enter version to package as (e.g. 4.0.0, empty = keep project version): "
if not defined VERSION goto :versiondone
echo(!VERSION!| findstr /r /x "[0-9][0-9]*\.[0-9][0-9]*\.0" >nul
if errorlevel 1 (
    echo [agent-limit-checker] Invalid version "!VERSION!". Use X.Y.0 like 4.0.0.
    goto :askversion
)
:versiondone

set "VEROPT="
if defined VERSION set "VEROPT=-p:Version=%VERSION%"
set "CURVER=%VERSION%"
if not defined CURVER set "CURVER=%PROJECTVER%"
echo(!CURVER!| findstr /r /x "[0-9][0-9]*\.[0-9][0-9]*\.0" >nul
if errorlevel 1 (
    echo [agent-limit-checker] ERROR: could not resolve a version in X.Y.0 form.
    goto :failed
)

set "TAG=v%CURVER%"
set "TAG_HASH="
set "TAG_TYPE="
set "TAG_EXISTS="
git show-ref --verify --quiet "refs/tags/%TAG%"
if not errorlevel 1 (
    set "TAG_EXISTS=1"
    for /f "delims=" %%H in ('git rev-list -n 1 "refs/tags/%TAG%"') do set "TAG_HASH=%%H"
    for /f "delims=" %%T in ('git cat-file -t "refs/tags/%TAG%"') do set "TAG_TYPE=%%T"
)
if defined TAG_EXISTS if /i not "!TAG_HASH!"=="!BUILD_HASH!" (
    echo [agent-limit-checker] ERROR: %TAG% points to a different commit.
    goto :failed
)
if defined TAG_EXISTS if not "!TAG_TYPE!"=="tag" (
    echo [agent-limit-checker] ERROR: %TAG% must be an annotated tag.
    goto :failed
)

node --use-system-ca tools\release-site.js check-version --version %CURVER%
if errorlevel 1 goto :failed

dotnet test AgentLimitChecker.slnx -m:1 -nr:false --blame-hang-timeout 60s
if errorlevel 1 goto :failed
node --test tests/tools/*.test.js
if errorlevel 1 goto :failed

if defined VERSION (
    echo [agent-limit-checker] Publishing Release as version %VERSION%...
) else (
    echo [agent-limit-checker] Publishing Release with the project version...
)
if exist "dist\package" rmdir /s /q "dist\package"
dotnet publish dotnet\AgentLimitChecker.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded %VEROPT% -o dist\package
if errorlevel 1 goto :failed

set "RELEASED_AT="
for /f "delims=" %%D in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd"') do set "RELEASED_AT=%%D"
if not defined RELEASED_AT (
    echo [agent-limit-checker] ERROR: could not determine the release date.
    goto :failed
)
node tools\release-site.js generate-pages --version %CURVER% --released-at %RELEASED_AT% --out artifacts\site-stage
if errorlevel 1 goto :failed
copy /y "artifacts\site-stage\manual.html" "dist\package\manual.html" >nul
if errorlevel 1 goto :failed
copy /y "artifacts\site-stage\license.html" "dist\package\license.html" >nul
if errorlevel 1 goto :failed

set "ENTRYCOUNT=0"
for /f "delims=" %%E in ('dir /b /a "dist\package" 2^>nul') do set /a ENTRYCOUNT+=1
if not "!ENTRYCOUNT!"=="3" (
    echo [agent-limit-checker] ERROR: distribution output must contain exactly 3 files; found !ENTRYCOUNT!.
    dir /b /a "dist\package"
    goto :failed
)
if not exist "dist\package\AgentLimitChecker.exe" (
    echo [agent-limit-checker] ERROR: AgentLimitChecker.exe is missing.
    goto :failed
)
if not exist "dist\package\manual.html" (
    echo [agent-limit-checker] ERROR: manual.html is missing.
    goto :failed
)
if not exist "dist\package\license.html" (
    echo [agent-limit-checker] ERROR: license.html is missing.
    goto :failed
)

if not exist "build\release" mkdir "build\release"
set "ZIP=build\release\AgentLimitChecker-%CURVER%-win-x64.zip"
if exist "!ZIP!" del /q "!ZIP!"
powershell -NoProfile -Command "Compress-Archive -Path 'dist\package\*' -DestinationPath '!ZIP!'"
if errorlevel 1 goto :failed
powershell -NoProfile -Command "$archive=[IO.Compression.ZipFile]::OpenRead('!ZIP!'); try { $entries=$archive.Entries; if ($entries.Count -ne 3 -or @($entries | Where-Object { $_.FullName -notin @('AgentLimitChecker.exe','manual.html','license.html') }).Count -ne 0) { Write-Error 'zip must contain only AgentLimitChecker.exe, manual.html and license.html'; exit 1 } } finally { $archive.Dispose() }"
if errorlevel 1 goto :failed

node tools\release-site.js generate --version %CURVER% --released-at %RELEASED_AT% --out build\release --zip "!ZIP!"
if errorlevel 1 goto :failed
set "AGENT_LIMIT_CHECKER_TEST_MANIFEST_PATH=%CD%\build\release\update-v2.json"
dotnet test AgentLimitChecker.slnx -m:1 -nr:false --no-restore --filter "FullyQualifiedName~Parse_ReleaseSiteGeneratedManifest_IsAccepted"
if errorlevel 1 goto :failed
set "AGENT_LIMIT_CHECKER_TEST_MANIFEST_PATH="

echo.
echo [agent-limit-checker] Package completed.
echo   zip:         !ZIP!
echo   index.html:  build\release\index.html
echo   manual.html: build\release\manual.html
echo   license.html: build\release\license.html
echo   update-v2.json: build\release\update-v2.json
echo.
set "UPLOAD="
set /p "UPLOAD=Create the GitHub Release and upload the site files now? (y/N): "
if /i not "!UPLOAD!"=="y" (
    echo [agent-limit-checker] Upload skipped. The package and generated files are kept.
    goto :done
)

if not defined TAG_EXISTS (
    git tag -a "%TAG%" "%BUILD_HASH%" -m "Release %TAG%"
    if errorlevel 1 goto :tagcreatefailed
)
git push origin "refs/tags/%TAG%"
if errorlevel 1 goto :tagpushfailed

node --use-system-ca tools\release-site.js upload --version %CURVER% --out build\release --zip "!ZIP!"
if errorlevel 1 goto :uploadfailed
echo [agent-limit-checker] GitHub Release and site upload completed.
goto :done

:uploadfailed
echo.
echo [agent-limit-checker] Upload failed. The package and tag are kept.
echo Retry: npm run release:upload -- --version %CURVER% --out build\release --zip "!ZIP!"
goto :pausefail

:tagcreatefailed
echo [agent-limit-checker] Upload was skipped because %TAG% could not be created.
goto :pausefail

:tagpushfailed
echo [agent-limit-checker] Upload was skipped because %TAG% could not be pushed.
echo Retry: git push origin "refs/tags/%TAG%"
goto :pausefail

:done
pause
exit /b 0

:failed
echo.
echo [agent-limit-checker] Packaging failed. Check the output above.
:pausefail
pause
exit /b 1
