# Construieste installerul agentului: publicare self-contained (win-x64) a agentului si a helper-ului
# de blocare, apoi Inno Setup. Rezultat: artifacts/TailControl-Agent-Setup-<versiune>.exe
# Utilizare (din radacina repo-ului):  powershell -ExecutionPolicy Bypass -File deploy/agent/build-installer.ps1 -Version 0.1.0
param(
    [string] $Version = '0.0.0',
    [string] $Iscc = ''
)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts 'agent-staging'

if (-not $Iscc) {
    $Iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) nu a fost gasit. Instaleaza-l sau da calea cu -Iscc.' }

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }

foreach ($project in 'TailControl.Agent', 'TailControl.LockHelper') {
    dotnet publish (Join-Path $root "src\$project") -c Release -r win-x64 --self-contained `
        -p:Version=$Version -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project a esuat" }
}

# Configurarea de dezvoltare si exemplele nu se instaleaza.
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $staging 'appsettings.Development.json'), (Join-Path $staging 'appsettings.example.json')
Copy-Item (Join-Path $PSScriptRoot 'renew-cert.ps1') $staging

& $Iscc "/DAppVersion=$Version" "/DSourceDir=$staging" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'TailControlAgent.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup a esuat' }

Write-Host "Installer: $(Join-Path $artifacts "TailControl-Agent-Setup-$Version.exe")"
