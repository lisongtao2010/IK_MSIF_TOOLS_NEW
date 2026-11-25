@echo off
rem =====================================================================
rem  MSIF Tools - one-click release.  Double-click this file.
rem  No arguments needed. No PowerShell knowledge needed.
rem
rem  It calls Tools\Publish-Signed.ps1 -SelfSigned, which does:
rem    build Release -> blank secrets -> sign (self-signed cert auto picked)
rem    -> add trusted-publisher bundle -> zip -> false-positive kit
rem  Output: artifacts\publish\MSIFAITOOLS_<version>_<time>.zip
rem
rem  Optional switches are passed through, e.g.
rem     publish.bat -SkipSign              (no signing at all)
rem     publish.bat -SecretMode BlankAll   (also blank SQL passwords)
rem =====================================================================
setlocal
title MSIF Tools - release
cd /d "%~dp0"

if not exist "Tools\Publish-Signed.ps1" goto nops

echo =====================================================================
echo   Building the release package.  This takes a few minutes.
echo   Please do not close this window.
echo =====================================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "Tools\Publish-Signed.ps1" -SelfSigned %*
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" goto failed

echo [OK] Package is ready. Opening the output folder ...
if not defined MSIF_NO_SHELL_OPEN start "" explorer "%~dp0artifacts\publish"
goto end

:failed
echo [FAILED] exit code %RC%
echo Scroll up and read the red error message.
echo.

:end
echo Press any key to close this window.
pause >nul
exit /b %RC%

:nops
echo ERROR: Tools\Publish-Signed.ps1 was not found next to this file.
echo Put publish.bat inside the MSIFAITOOLS folder (next to MSIFAITOOLS.csproj).
echo.
pause >nul
exit /b 1
