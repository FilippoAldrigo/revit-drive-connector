$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $ScriptDir

$DotnetCmd = "dotnet"
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
    $DotnetCmd = "$env:USERPROFILE\.dotnet\dotnet.exe"
}

Write-Host "--- 1. Arresto processi RevitDriveConnector di sfondo ---" -ForegroundColor Cyan
Get-Process -Name "*RevitDriveConnector*" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "*Setup*" -and $_.Path -like "*RevitDriveConnector*" } | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "--- 2. Compilazione Progetti di Base (Core, Daemon, Server, Addin) ---" -ForegroundColor Cyan
$binDir = Join-Path $root "dist\payload\bin"
New-Item -ItemType Directory -Path $binDir -Force | Out-Null

& $DotnetCmd build "$root\RevitDriveConnector.slnx" -c Release

Copy-Item "$root\src\RevitDriveConnector.Core\bin\Release\net8.0\*" $binDir -Force -Recurse
Copy-Item "$root\src\RevitDriveConnector.Daemon\bin\Release\net8.0\*" $binDir -Force -Recurse
Copy-Item "$root\src\RevitDriveConnector.Server\bin\Release\net8.0\*" $binDir -Force -Recurse
Copy-Item "$root\src\RevitDriveConnector.Addin\bin\Release\net10.0-windows\*" $binDir -Force -Recurse

# Esclude credentials.json dal pacchetto pubblico (ogni utente/studio configura il proprio)
$binCreds = Join-Path $binDir "credentials.json"
if (Test-Path $binCreds) {
    Remove-Item $binCreds -Force
}

Write-Host "--- 3. Creazione payload.zip per Installer ---" -ForegroundColor Cyan
$resDir = Join-Path $root "src\RevitDriveConnector.Installer\Resources"
New-Item -ItemType Directory -Path $resDir -Force | Out-Null
$zipFile = Join-Path $resDir "payload.zip"
if (Test-Path $zipFile) {
    Remove-Item $zipFile -Force
}

Compress-Archive -Path "$binDir\*" -DestinationPath $zipFile -Force

Write-Host "--- 4. Creazione Eseguibile Setup Standalone (.NET 8 Compatibility) ---" -ForegroundColor Cyan
$standaloneDir = Join-Path $root "dist\standalone"
New-Item -ItemType Directory -Path $standaloneDir -Force | Out-Null
if (Test-Path "$standaloneDir\*") { Remove-Item "$standaloneDir\*" -Force -Recurse -ErrorAction SilentlyContinue }
& $DotnetCmd publish "$root\src\RevitDriveConnector.Installer\RevitDriveConnector.Installer.csproj" -c Release -f net8.0-windows -r win-x64 --self-contained false -p:PublishSingleFile=true -o $standaloneDir

# Build standard anche nella cartella dist
& $DotnetCmd build "$root\src\RevitDriveConnector.Installer\RevitDriveConnector.Installer.csproj" -c Release -o "$root\dist"

Write-Host "--- 5. Aggiornamento Copia Locale %APPDATA%\RevitDriveConnector\bin ---" -ForegroundColor Cyan
$appDataBin = Join-Path $env:APPDATA "RevitDriveConnector\bin"
New-Item -ItemType Directory -Path $appDataBin -Force | Out-Null
try {
    Copy-Item "$binDir\*" $appDataBin -Force -Recurse -ErrorAction Stop
    Write-Host "Copia in AppData aggiornata con successo." -ForegroundColor Green
}
catch {
    Write-Warning "Impossibile aggiornare i file in AppData perché Revit è in esecuzione. Verranno aggiornati al prossimo riavvio di Revit o eseguendo l'installer."
}

Write-Host "=== PACCHETTO CREATO CON SUCCESSO! ===" -ForegroundColor Green
Write-Host "Setup EXE Standalone: $standaloneDir\RevitDriveConnector_Setup.exe"
Write-Host "Setup EXE Dist: $root\dist\RevitDriveConnector_Setup.exe"
