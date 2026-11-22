param(
    [string]$ApiUrl = "https://agres.home.arpa/"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$CsprojPath = Join-Path $RepoRoot "POS.Client\POS.Client.csproj"

[xml]$csproj = Get-Content $CsprojPath
$displayVersion = ($csproj.Project.PropertyGroup | ForEach-Object { $_.ApplicationDisplayVersion } | Where-Object { $_ } | Select-Object -First 1).Trim()
if (-not $ApiUrl.EndsWith("/")) { $ApiUrl = "$ApiUrl/" }

$releaseWindowsDir = Join-Path $RepoRoot "Releases\v$displayVersion\Windows"
$publishStageDir = Join-Path $RepoRoot "publish\client-windows"
New-Item -ItemType Directory -Force -Path $releaseWindowsDir | Out-Null

Write-Host "=== 1. Restoring & Publishing POS.Client v$displayVersion (Release win-x64, API=$ApiUrl) ===" -ForegroundColor Cyan
Push-Location $RepoRoot
try {
    dotnet restore POS.Client/POS.Client.csproj -p:Configuration=Release -p:PosApiBaseUrl=$ApiUrl
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    dotnet publish POS.Client/POS.Client.csproj -f net10.0-windows10.0.19041.0 -c Release -r win-x64 --self-contained true --no-restore -p:WindowsPackageType=None -p:PosApiBaseUrl=$ApiUrl -p:DebugType=none -p:DebugSymbols=false -o $publishStageDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $possiblePaths = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $isccPath = $possiblePaths | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $isccPath) {
        $isccCmd = Get-Command iscc -ErrorAction SilentlyContinue
        if ($isccCmd) { $isccPath = $isccCmd.Source }
    }

    if ($isccPath -and (Test-Path $isccPath)) {
        Write-Host "=== 2. Compiling Versioned Single-File Setup Installer (v$displayVersion) ===" -ForegroundColor Cyan
        & $isccPath "/DMyAppVersion=$displayVersion" "/DMyOutputDir=$releaseWindowsDir" "deployment\installer.iss"
        if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }
        Write-Host "`nSUCCESS: Installer created at $releaseWindowsDir\POS_Client_Setup_v$displayVersion.exe" -ForegroundColor Green
    } else {
        Write-Host "`n[NOTE] Inno Setup compiler (ISCC.exe) was not found." -ForegroundColor Yellow
        Write-Host "Please install Inno Setup by running: winget install JRSoftware.InnoSetup" -ForegroundColor Yellow
        Write-Host "Then compile deployment\installer.iss with /DMyAppVersion=$displayVersion /DMyOutputDir=$releaseWindowsDir" -ForegroundColor Yellow
    }
}
finally {
    Pop-Location
}
