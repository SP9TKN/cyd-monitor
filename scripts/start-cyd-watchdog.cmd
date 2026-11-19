@echo off
rem Wlacza watchdoga CYD: rejestruje zadanie "CYD-Watchdog" (start po zalogowaniu + kontrola
rem co 5 min); serwer wstaje sam po kazdym restarcie komputera. Cofniecie: stop-cyd-watchdog.cmd.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-cyd-watchdog.ps1" %*
set KOD=%ERRORLEVEL%
echo.
pause
exit /b %KOD%
