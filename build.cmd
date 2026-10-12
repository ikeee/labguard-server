@echo off
REM LabGuard Server one-click build (plan LG-002). Same as: powershell -File build.ps1 %*
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
