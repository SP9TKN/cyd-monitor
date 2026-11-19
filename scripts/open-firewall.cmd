@echo off
rem Reguly zapory dla serwera CYD Monitor (robi to juz enable-service.cmd; tu osobno, gdyby trzeba bylo powtorzyc).
net session >nul 2>&1 || (powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" & exit /b)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0open-firewall.ps1" %*
set KOD=%ERRORLEVEL%
echo.
pause
exit /b %KOD%
