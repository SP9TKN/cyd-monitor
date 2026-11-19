@echo off
rem Wylacza usluge CYD Monitor: zatrzymuje serwer i autostart (pyta o administratora). Wlaczenie: enable-service.cmd.
net session >nul 2>&1 || (powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" & exit /b)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0disable-service.ps1" %*
set KOD=%ERRORLEVEL%
echo.
pause
exit /b %KOD%
