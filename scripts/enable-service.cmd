@echo off
rem Wlacza usluge CYD Monitor: serwer startuje teraz i po kazdym restarcie (pyta o administratora). Wylaczenie: disable-service.cmd.
net session >nul 2>&1 || (powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" & exit /b)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0enable-service.ps1" %*
set KOD=%ERRORLEVEL%
echo.
pause
exit /b %KOD%
