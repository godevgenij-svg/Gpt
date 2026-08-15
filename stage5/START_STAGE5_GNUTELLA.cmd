@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0START_STAGE5_GNUTELLA.ps1"
exit /b %errorlevel%
