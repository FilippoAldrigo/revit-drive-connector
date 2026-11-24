@echo off
title Caricamento Revit Drive Connector su GitHub
cd /d "%~dp0"
echo ========================================================
echo  CARICAMENTO NUOVA VERSIONE v1.4.0 SU GITHUB
echo ========================================================
echo.
git log -1 --oneline
echo.
echo Invio commit, tag e aggiornamento sito GitHub Pages su origin/main...
git push origin main --tags
echo.
if %ERRORLEVEL% equ 0 (
    echo ========================================================
    echo  SUCCESSO! La versione v1.4.0 e il sito web sono aggiornati.
    echo ========================================================
) else (
    echo.
    echo Se compare una richiesta di accesso GitHub, completa l'autorizzazione nel browser.
)
echo.
pause
