@echo off
rem Uruchamia serwer CYD Monitor (usluge) teraz - ukryty, niezalezny od tego okna.
rem Zatrzymanie: stop-cyd.cmd. Autostart po restarcie komputera: start-cyd-watchdog.cmd.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-cyd.ps1" %*
set KOD=%ERRORLEVEL%
echo.
pause
exit /b %KOD%
