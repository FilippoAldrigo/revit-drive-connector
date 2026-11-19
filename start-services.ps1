# Script per avviare il Coordinator Server e il Local Daemon di RevitDriveConnector

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppDataBin = Join-Path $env:APPDATA "RevitDriveConnector\bin"
$DistBin = Join-Path $ScriptDir "dist\payload\bin"

$BinDir = if (Test-Path (Join-Path $AppDataBin "RevitDriveConnector.Daemon.exe")) { $AppDataBin } else { $DistBin }
$ServerExe = Join-Path $BinDir "RevitDriveConnector.Server.exe"
$DaemonExe = Join-Path $BinDir "RevitDriveConnector.Daemon.exe"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   AVVIO SERVIZI REVIT GOOGLE DRIVE CONNECTOR             " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Avvia Coordinator Server in una nuova finestra
Write-Host "[1/2] Avvio Coordinator Lock Server (porta 5000)..." -ForegroundColor Yellow
if (Test-Path $ServerExe) {
    Start-Process -FilePath $ServerExe -WorkingDirectory $BinDir
} else {
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "dotnet run --project '$ScriptDir\src\RevitDriveConnector.Server'"
}

Start-Sleep -Seconds 2

# 2. Avvia Local Daemon in una nuova finestra
Write-Host "[2/2] Avvio Local IPC Daemon..." -ForegroundColor Yellow
if (Test-Path $DaemonExe) {
    Start-Process -FilePath $DaemonExe -WorkingDirectory $BinDir
} else {
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "dotnet run --project '$ScriptDir\src\RevitDriveConnector.Daemon'"
}

Write-Host ""
Write-Host "Servizi avviati con successo!" -ForegroundColor Green
Write-Host "Ora puoi avviare Revit: troverai la scheda 'Drive Connector' nella Ribbon." -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan
