# Construieste installerul aplicatiei Windows: MAUI unpackaged + Windows App SDK self-contained,
# apoi Inno Setup. Rezultat: artifacts/TailControl-Client-Setup-<versiune>.exe
# Utilizare (din radacina repo-ului):  powershell -ExecutionPolicy Bypass -File deploy/client-windows/build-installer.ps1 -Version 0.1.0
param(
    [string] $Version = '0.0.0',
    [string] $Iscc = ''
)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts 'client-windows-staging'

# MAUI cere pentru versiunea afisata strict trei numere (0.1.0-test -> 0.1.0).
$displayVersion = ($Version -split '[-+]')[0]

if (-not $Iscc) {
    $Iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) nu a fost gasit. Instaleaza-l sau da calea cu -Iscc.' }

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }

dotnet publish (Join-Path $root 'src\TailControl.Client') -f net10.0-windows10.0.19041.0 -c Release -p:RuntimeIdentifierOverride=win-x64 `
    -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:SelfContained=true `
    -p:Version=$Version -p:ApplicationDisplayVersion=$displayVersion -o $staging
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish a esuat' }

& $Iscc "/DAppVersion=$Version" "/DSourceDir=$staging" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'TailControlClient.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup a esuat' }

Write-Host "Installer: $(Join-Path $artifacts "TailControl-Client-Setup-$Version.exe")"
