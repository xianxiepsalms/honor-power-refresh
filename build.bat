@echo off
rem Double-click wrapper for build.ps1 (HonorPowerRefresh).
rem Pass any build.ps1 switch through, e.g.:
rem   build.bat                 compile exe + import scheduled task
rem   build.bat -RunTaskNow     same as Install-HonorPowerRefresh.ps1
rem   build.bat -TaskOnly       only re-import the task XML, no compile
rem   build.bat -SkipTaskSync   only compile, do not touch the task
rem   build.bat -Recreate       delete then re-import the task
setlocal
cd /d "%~dp0"

set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"

echo === HonorPowerRefresh build ===
echo Script dir : %CD%
echo Running    : build.ps1 %*
echo.

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
  echo Build finished successfully.
) else (
  echo Build FAILED with exit code %RC%. Check the messages above.
)
if "%~1"=="" (
  echo.
  echo Press any key to close this window...
  pause >nul
)
exit /b %RC%
