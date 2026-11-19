@echo off
rem Zatrzymuje serwer CYD Monitor (usluge). Panel zostaje na ostatnim obrazie.
rem Gdy watchdog jest wlaczony, wstanie ponownie w ciagu ok. 30 s - najpierw stop-cyd-watchdog.cmd.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0stop-cyd.ps1" %*
set KOD=%ERRORLEVEL%
echo.
pause
exit /b %KOD%
