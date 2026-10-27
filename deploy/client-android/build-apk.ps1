# Construieste APK-ul Android semnat cu cheia de release.
# Rezultat: artifacts/TailControl-<versiune>.apk
#
# Keystore-ul NU sta in repo. Se da prin variabile de mediu (in CI: GitHub Secrets):
#   ANDROID_KEYSTORE_BASE64   keystore-ul codat base64 (sau ANDROID_KEYSTORE_PATH = cale locala)
#   ANDROID_KEYSTORE_PASSWORD parola keystore-ului
#   ANDROID_KEY_ALIAS         aliasul cheii
#   ANDROID_KEY_PASSWORD      parola cheii
# Vezi docs/release.md pentru generarea keystore-ului.
#
# Utilizare:  pwsh -File deploy/client-android/build-apk.ps1 -Version 0.1.0 -VersionCode 3
param(
    [string] $Version = '0.0.0',
    [int] $VersionCode = 1
)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$artifacts = Join-Path $root 'artifacts'
$out = Join-Path $artifacts 'client-android'

foreach ($name in 'ANDROID_KEYSTORE_PASSWORD', 'ANDROID_KEY_ALIAS', 'ANDROID_KEY_PASSWORD') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Lipseste variabila de mediu $name (vezi docs/release.md)." }
}

$keystore = $env:ANDROID_KEYSTORE_PATH
$temporary = $false
if (-not $keystore) {
    if (-not $env:ANDROID_KEYSTORE_BASE64) { throw 'Lipseste ANDROID_KEYSTORE_BASE64 sau ANDROID_KEYSTORE_PATH (vezi docs/release.md).' }
    $keystore = Join-Path ([IO.Path]::GetTempPath()) "tailcontrol-$([guid]::NewGuid()).keystore"
    [IO.File]::WriteAllBytes($keystore, [Convert]::FromBase64String($env:ANDROID_KEYSTORE_BASE64))
    $temporary = $true
}

try {
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }

    # Parolele sunt citite de MSBuild direct din mediu (env:), nu apar in linia de comanda sau in log.
    dotnet publish (Join-Path $root 'src\TailControl.Client') -f net10.0-android -c Release `
        -p:AndroidPackageFormat=apk `
        -p:AndroidKeyStore=true `
        -p:AndroidSigningKeyStore=$keystore `
        -p:AndroidSigningKeyAlias=$env:ANDROID_KEY_ALIAS `
        -p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD `
        -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD `
        -p:Version=$Version -p:ApplicationDisplayVersion=$Version -p:ApplicationVersion=$VersionCode `
        -o $out
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish a esuat' }
}
finally {
    if ($temporary) { Remove-Item -Force -ErrorAction SilentlyContinue $keystore }
}

$apk = Get-ChildItem $out -Filter '*-Signed.apk' | Select-Object -First 1
if (-not $apk) { throw 'APK-ul semnat nu a fost gasit.' }
$target = Join-Path $artifacts "TailControl-$Version.apk"
Copy-Item -Force $apk.FullName $target
Write-Host "APK: $target"
