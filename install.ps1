# Script di installazione automatica con 1 clic per qualsiasi PC

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PayloadBinDir = Join-Path $ScriptDir "dist\payload\bin"
$AppDataDir = [System.IO.Path]::Combine($env:APPDATA, "RevitDriveConnector")
$InstallBinDir = Join-Path $AppDataDir "bin"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   INSTALLAZIONE DI REVIT GOOGLE DRIVE CONNECTOR          " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Arresta eventuali processi Daemon/Server in esecuzione
Get-Process -Name "RevitDriveConnector.Daemon", "RevitDriveConnector.Server" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 2. Compila se disponibile .NET SDK (64-bit) o usa i binari pacchettizzati in dist\payload\bin
$DotnetCmd = "dotnet"
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
    $DotnetCmd = "$env:USERPROFILE\.dotnet\dotnet.exe"
} elseif (Test-Path "$env:ProgramW6432\dotnet\dotnet.exe") {
    $DotnetCmd = "$env:ProgramW6432\dotnet\dotnet.exe"
}

$HasSdk = $false
try {
    $sdks = & $DotnetCmd --list-sdks 2>$null
    if ($sdks) { $HasSdk = $true }
} catch { }

if ($HasSdk -and ($env:PROCESSOR_ARCHITECTURE -eq "AMD64")) {
    Write-Host "Compilazione aggiornata dei progetti in corso..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $PayloadBinDir -Force | Out-Null
    & $DotnetCmd build (Join-Path $ScriptDir "RevitDriveConnector.slnx") -c Release | Out-Null
    Copy-Item (Join-Path $ScriptDir "src\RevitDriveConnector.Core\bin\Release\net8.0\*") $PayloadBinDir -Force -Recurse -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $ScriptDir "src\RevitDriveConnector.Daemon\bin\Release\net8.0\*") $PayloadBinDir -Force -Recurse -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $ScriptDir "src\RevitDriveConnector.Server\bin\Release\net8.0\*") $PayloadBinDir -Force -Recurse -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $ScriptDir "src\RevitDriveConnector.Addin\bin\Release\net10.0-windows\*") $PayloadBinDir -Force -Recurse -ErrorAction SilentlyContinue
}

if (!(Test-Path (Join-Path $PayloadBinDir "RevitDriveConnector.Addin.dll"))) {
    Write-Error "Impossibile trovare i file compilati in $PayloadBinDir"
    exit 1
}

# 3. Copia tutti gli eseguibili e le librerie in %APPDATA%\RevitDriveConnector\bin
New-Item -ItemType Directory -Path $InstallBinDir -Force | Out-Null
Copy-Item -Path "$PayloadBinDir\*" -Destination $InstallBinDir -Recurse -Force

$CredsSource = Join-Path $InstallBinDir "credentials.json"
$CredsDest = Join-Path $AppDataDir "credentials.json"
if ((Test-Path $CredsSource) -and !(Test-Path $CredsDest)) {
    Copy-Item -Path $CredsSource -Destination $CredsDest -Force
}

$AddinDll = Join-Path $InstallBinDir "RevitDriveConnector.Addin.dll"

# 4. Rileva le versioni di Revit installate (Program Files 64-bit o AppData) e registra il manifest .addin
$ProgFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$RevitVersions = @("2027", "2026", "2025")
$DetectedVersions = @()

foreach ($ver in $RevitVersions) {
    $progFolder = Join-Path $ProgFiles64 "Autodesk\Revit $ver"
    $addinFolder = [System.IO.Path]::Combine($env:APPDATA, "Autodesk", "Revit", "Addins", $ver)
    if ((Test-Path $progFolder) -or (Test-Path $addinFolder)) {
        $DetectedVersions += $ver
    }
}

if ($DetectedVersions.Count -eq 0) {
    $DetectedVersions = $RevitVersions
}

foreach ($ver in $DetectedVersions) {
    $addinFolder = [System.IO.Path]::Combine($env:APPDATA, "Autodesk", "Revit", "Addins", $ver)
    New-Item -ItemType Directory -Path $addinFolder -Force | Out-Null
    $manifestPath = Join-Path $addinFolder "RevitDriveConnector.addin"

    $manifestContent = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Google Drive Connector for Revit $ver</Name>
    <Assembly>$AddinDll</Assembly>
    <AddInId>8B12F0C1-6028-44A5-B7D4-1B155B907E92</AddInId>
    <FullClassName>RevitDriveConnector.Addin.App</FullClassName>
    <VendorId>REVIT_DRIVE_CONNECTOR</VendorId>
    <VendorDescription>OpenCloud Worksharing Connector for Autodesk Revit</VendorDescription>
    <UseRevitContext>false</UseRevitContext>
  </AddIn>
</RevitAddIns>
"@
    Set-Content -Path $manifestPath -Value $manifestContent -Encoding UTF8 -Force
    Write-Host " [OK] Plugin registrato per Revit $ver in: $manifestPath" -ForegroundColor Green
}

# 5. Inizializza cartelle di default
$cacheDir = "C:\RevitDriveCache"
New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "   INSTALLAZIONE COMPLETATA CON SUCCESSO!                  " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "File del plugin installati in: $InstallBinDir"
Write-Host "Ora puoi avviare Revit: troverai la scheda 'Drive Connector' nella Ribbon."
