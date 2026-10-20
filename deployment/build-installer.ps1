param(
    [string]$ApiUrl = "http://localhost:5032/"
)

$ErrorActionPreference = "Stop"

Write-Host "=== 1. Publishing POS.Client (Release win-x64) ===" -ForegroundColor Cyan
dotnet publish POS.Client/POS.Client.csproj -f net10.0-windows10.0.19041.0 -c Release -p:WindowsPackageType=None -o ./publish/client-windows

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
    Write-Host "=== 2. Compiling Single-File Setup Installer ===" -ForegroundColor Cyan
    & $isccPath "deployment\installer.iss"
    Write-Host "`nSUCCESS: Installer created at ./publish/installer/POS_Client_Setup.exe" -ForegroundColor Green
} else {
    Write-Host "`n[NOTE] Inno Setup compiler (ISCC.exe) was not found." -ForegroundColor Yellow
    Write-Host "Please install Inno Setup by running: winget install JRSoftware.InnoSetup" -ForegroundColor Yellow
    Write-Host "Then double-click 'deployment\installer.iss' and press Ctrl+F9 to compile." -ForegroundColor Yellow
}
