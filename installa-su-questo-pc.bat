@echo off
echo ========================================================
echo   INSTALLATORE REVIT DRIVE CONNECTOR (PER QUESTO PC)
echo ========================================================
echo.

if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" (
    "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -ExecutionPolicy Bypass -File "%~dp0install.ps1"
) else (
    powershell -ExecutionPolicy Bypass -File "%~dp0install.ps1"
)

pause
