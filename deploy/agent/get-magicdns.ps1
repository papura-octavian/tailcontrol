# Folosit de installer ca sa precompleteze numele MagicDNS al acestui PC.
param([Parameter(Mandatory)] [string] $Out)
try {
    $tailscale = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
    $name = ((& $tailscale status --json | ConvertFrom-Json).Self.DNSName).TrimEnd('.').ToLowerInvariant()
    Set-Content -Path $Out -Value $name -Encoding ascii -NoNewline
} catch {
    Set-Content -Path $Out -Value '' -Encoding ascii -NoNewline
}
