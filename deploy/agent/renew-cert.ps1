# Obtine/reinnoieste certificatul HTTPS al agentului prin `tailscale cert`.
# Rulat ca SYSTEM de task-ul programat "TailControl\AgentCertRenew" (saptamanal) si de installer.
# Agentul recitesc fisierele automat, fara repornire. tailscale cert reinnoieste efectiv doar
# cand certificatul e aproape de expirare.
$ErrorActionPreference = 'Stop'

$configDir = Join-Path $env:ProgramData 'TailControl\Agent\config'
$agentJson = Join-Path $configDir 'agent.json'
if (-not (Test-Path $agentJson)) { throw "Lipseste $agentJson." }

$domain = ((Get-Content $agentJson -Raw | ConvertFrom-Json).AllowedHosts).Trim()
if (-not $domain -or $domain -notmatch '^[a-z0-9.-]+\.ts\.net$') {
    throw "AllowedHosts din $agentJson trebuie sa fie numele MagicDNS al acestui PC."
}

$tailscale = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
if (-not (Test-Path $tailscale)) { throw "Nu gasesc $tailscale." }

$crt = Join-Path $configDir 'agent.crt'
$key = Join-Path $configDir 'agent.key'
& $tailscale cert --cert-file "$crt.new" --key-file "$key.new" $domain
if ($LASTEXITCODE -ne 0) { throw "tailscale cert a esuat (cod $LASTEXITCODE). Este HTTPS activat in consola Tailscale?" }

# Fisierele noi mostenesc ACL-ul folderului config (serviciul doar citeste).
Move-Item -Force "$key.new" $key
Move-Item -Force "$crt.new" $crt
Write-Output "Certificat actualizat pentru $domain."
