param(
    [int]$MinVersionCode = 3,
    [string]$ExpectedPackageId = "com.agres.pos.client",
    [string]$ExpectedCertSha256 = "9A:74:09:2B:9A:F0:A0:B9:1C:1A:96:2C:63:B3:3D:E4:01:2C:60:68:FF:1E:19:B9:64:8A:C0:AA:F2:C7:E7:57"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$CsprojPath = Join-Path $RepoRoot "POS.Client\POS.Client.csproj"

[xml]$csproj = Get-Content $CsprojPath
$appId = ($csproj.Project.PropertyGroup | ForEach-Object { $_.ApplicationId } | Where-Object { $_ } | Select-Object -First 1).Trim()
$displayVersion = ($csproj.Project.PropertyGroup | ForEach-Object { $_.ApplicationDisplayVersion } | Where-Object { $_ } | Select-Object -First 1).Trim()
$versionCodeRaw = ($csproj.Project.PropertyGroup | ForEach-Object { $_.ApplicationVersion } | Where-Object { $_ } | Select-Object -First 1).Trim()
$versionCode = [int]$versionCodeRaw

if ($appId -cne $ExpectedPackageId) {
    throw "CRITICAL: ApplicationId is '$appId', expected '$ExpectedPackageId'. Changing ApplicationId prevents Android in-place upgrades and strands local_pos.db!"
}

if ($versionCode -lt $MinVersionCode) {
    throw "CRITICAL: ApplicationVersion (versionCode) is $versionCode, which is less than minimum required $MinVersionCode. Android blocks APK updates unless versionCode >= installed versionCode."
}

Write-Host "=== Building Universal Android APK ($appId v$displayVersion, versionCode=$versionCode) ===" -ForegroundColor Cyan

Push-Location $RepoRoot
try {
    dotnet restore POS.Client\POS.Client.csproj -p:Configuration=Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    dotnet publish POS.Client\POS.Client.csproj -f net10.0-android -c Release --no-restore -p:AndroidPackageFormat=apk -p:AndroidKeyStore=false -p:DebugType=none -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $unsignedApk = Join-Path $RepoRoot "POS.Client\bin\Release\net10.0-android\$appId.apk"
    if (-not (Test-Path $unsignedApk)) {
        throw "Unsigned APK not found at $unsignedApk"
    }

    $alignedApk = Join-Path $RepoRoot "POS.Client\bin\Release\net10.0-android\$appId-aligned.apk"
    $sdkBuildTools = "C:\Program Files (x86)\Android\android-sdk\build-tools\36.0.0"
    $zipalign = Join-Path $sdkBuildTools "zipalign.exe"
    $apksigner = Join-Path $sdkBuildTools "apksigner.bat"
    $env:JAVA_HOME = "C:\Program Files\Android\openjdk\jdk-21.0.8"

    & $zipalign -f -p 4 $unsignedApk $alignedApk
    if ($LASTEXITCODE -ne 0) { throw "zipalign failed on $unsignedApk" }

    & $zipalign -c 4 $alignedApk
    if ($LASTEXITCODE -ne 0) { throw "zipalign verification failed on $alignedApk" }

    $releaseDir = Join-Path $RepoRoot "Releases\v$displayVersion\Android"
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
    $signedApk = Join-Path $releaseDir "$appId-v$displayVersion-universal-signed.apk"

    $keystoreDir = Join-Path $env:USERPROFILE ".pos-signing\pos-client-release"
    $keystorePath = Join-Path $keystoreDir "pos-client-release.jks"
    $credsFile = Join-Path $keystoreDir "keystore-credentials.txt"

    if (-not (Test-Path $keystorePath)) {
        throw "Release keystore not found at $keystorePath"
    }

    if (-not $env:POS_KS_PASS -and (Test-Path $credsFile)) {
        $passLine = Get-Content $credsFile | Where-Object { $_ -match "^Password\s*:\s*(.+)$" } | Select-Object -First 1
        if ($passLine -match "^Password\s*:\s*(.+)$") {
            $env:POS_KS_PASS = $Matches[1].Trim()
        }
    }

    if ($env:POS_KS_PASS) {
        & $apksigner sign --ks $keystorePath --ks-key-alias pos-release --ks-pass env:POS_KS_PASS --key-pass env:POS_KS_PASS --v1-signing-enabled true --v2-signing-enabled true --v3-signing-enabled true --v4-signing-enabled false --out $signedApk $alignedApk
    } else {
        & $apksigner sign --ks $keystorePath --ks-key-alias pos-release --v1-signing-enabled true --v2-signing-enabled true --v3-signing-enabled true --v4-signing-enabled false --out $signedApk $alignedApk
    }
    if ($LASTEXITCODE -ne 0) { throw "apksigner sign failed with exit code $LASTEXITCODE" }

    $verifyOut = & $apksigner verify --verbose --print-certs $signedApk 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "apksigner verify failed:`n$verifyOut" }

    $cleanExpectedCert = $ExpectedCertSha256.Replace(":", "").ToLowerInvariant()
    if ($verifyOut.ToLowerInvariant() -notmatch $cleanExpectedCert) {
        throw "CRITICAL: Signed APK certificate SHA-256 does not match expected production certificate ($ExpectedCertSha256). Updating existing tablets with a mismatched certificate will fail!"
    }

    $sha256 = (Get-FileHash -Path $signedApk -Algorithm SHA256).Hash
    $sizeBytes = (Get-Item $signedApk).Length
    Write-Host "SUCCESS: Signed Universal APK ready at $signedApk" -ForegroundColor Green
    Write-Host "Size   : $sizeBytes bytes"
    Write-Host "SHA256 : $sha256"
}
finally {
    Remove-Item Env:\POS_KS_PASS -ErrorAction SilentlyContinue
    Pop-Location
}
