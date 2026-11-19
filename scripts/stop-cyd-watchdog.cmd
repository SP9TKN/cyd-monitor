@echo off
rem Wylacza watchdoga CYD (usuwa zadanie "CYD-Watchdog"). Serwer CYD dziala dalej.
rem Zatrzymanie serwera: stop-cyd.cmd.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0stop-cyd-watchdog.ps1" %*
set KOD=%ERRORLEVEL%
echo.
pause
exit /b %KOD%
