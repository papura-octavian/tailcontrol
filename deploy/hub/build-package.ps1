# Construieste pachetul Hub pentru Raspberry Pi (linux-arm, self-contained, ReadyToRun).
# Rezultat: artifacts/tailcontrol-hub-linux-arm.tar.gz + .sha256
# Utilizare (din radacina repo-ului):  powershell -File deploy/hub/build-package.ps1 [-Version 0.1.0]
param([string] $Version = '0.0.0')
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$staging = Join-Path $root 'artifacts\tailcontrol-hub-linux-arm'
$archive = Join-Path $root 'artifacts\tailcontrol-hub-linux-arm.tar.gz'

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force (Join-Path $staging 'deploy') | Out-Null

dotnet publish (Join-Path $root 'src\TailControl.Hub') -c Release -r linux-arm -p:Version=$Version -o (Join-Path $staging 'app')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish a esuat' }

# Configurarea de dezvoltare si exemplele nu au ce cauta pe Pi.
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $staging 'app\appsettings.Development.json')

$deploy = Join-Path $root 'deploy\hub'
Copy-Item (Join-Path $deploy 'install.sh'), (Join-Path $deploy 'uninstall.sh') $staging
Copy-Item (Join-Path $deploy '*.service'), (Join-Path $deploy '*.timer'), (Join-Path $deploy 'renew-cert.sh'),
          (Join-Path $deploy 'hub.example.json'), (Join-Path $deploy 'cert.env.example') (Join-Path $staging 'deploy')

if (Test-Path $archive) { Remove-Item -Force $archive }
tar -czf $archive -C (Join-Path $root 'artifacts') 'tailcontrol-hub-linux-arm'
if ($LASTEXITCODE -ne 0) { throw 'tar a esuat' }

$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  tailcontrol-hub-linux-arm.tar.gz" | Out-File -Encoding ascii "$archive.sha256"
Write-Host "Pachet: $archive"
Write-Host "SHA-256: $hash"
