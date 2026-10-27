# Genereaza keystore-ul Android de release si seteaza cele 4 secrete ANDROID_* in GitHub,
# fara tastare si fara copy-paste de parole (sursa obisnuita de greseli).
#
#   1. genereaza o parola aleatoare de 32 de caractere (litere si cifre, fara caractere ambigue)
#   2. creeaza keystore-ul cu keytool, parola fiind data prin variabila de mediu (nu prin argumente)
#   3. verifica local ca keystore-ul se deschide cu acea parola si contine aliasul
#   4. incarca secretele cu `gh secret set`, citind valorile din fisiere temporare (fara newline)
#   5. afiseaza parola o singura data, ca s-o salvezi in managerul de parole
#
# Ruleaza-l in PowerShell, din radacina repo-ului, autentificat cu `gh auth login`:
#   powershell -ExecutionPolicy Bypass -File deploy/client-android/new-signing-key.ps1
# Keystore-ul se scrie in afara repo-ului (implicit $HOME\tailcontrol-keys).
param(
    [string] $Repo = 'papura-octavian/tailcontrol',
    [string] $OutDir = (Join-Path $HOME 'tailcontrol-keys'),
    [string] $Alias = 'tailcontrol',
    # Doar pentru testarea scriptului: genereaza si verifica, dar nu incarca nimic in GitHub.
    [switch] $SkipUpload
)
$ErrorActionPreference = 'Stop'

$keytool = @(
    $(if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin\keytool.exe' }),
    (Get-ChildItem "$env:ProgramFiles\Android\openjdk\*\bin\keytool.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName),
    (Get-ChildItem "$env:ProgramFiles\Java\*\bin\keytool.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName)
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $keytool) { throw 'Nu gasesc keytool.exe (vine cu JDK-ul instalat de Android SDK / Visual Studio).' }

if (-not $SkipUpload) {
    gh auth status --hostname github.com *> $null
    if ($LASTEXITCODE -ne 0) { throw 'gh nu e autentificat in acest terminal. Ruleaza intai: gh auth login' }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$keystore = Join-Path $OutDir 'tailcontrol-release.keystore'
if (Test-Path $keystore) { throw "Exista deja $keystore. Muta-l sau sterge-l intai (nu suprascriu un keystore)." }

# Parola: 32 de caractere din 56 posibile (~186 biti), fara 0/O, 1/l/I, care se confunda la citire.
$alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
$random = New-Object byte[] 64
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($random)
$rng.Dispose()
# Pastram doar octetii < 224 (4 x 56), ca fiecare caracter sa fie la fel de probabil.
$password = -join ($random | Where-Object { $_ -lt 224 } | Select-Object -First 32 | ForEach-Object { $alphabet[$_ % 56] })
if ($password.Length -ne 32) { throw 'Generarea parolei a esuat; ruleaza din nou.' }

$env:TC_KEYSTORE_PASSWORD = $password
$temp = $null
try {
    # PKCS12: cheia primeste aceeasi parola ca keystore-ul.
    & $keytool -genkeypair -keystore $keystore -storetype PKCS12 -alias $Alias -keyalg RSA -keysize 4096 -validity 10000 `
        -dname 'CN=TailControl' -storepass:env TC_KEYSTORE_PASSWORD -noprompt
    if ($LASTEXITCODE -ne 0) { throw 'keytool -genkeypair a esuat.' }

    # Verificare: exact ce face si CI-ul inainte de semnare.
    & $keytool -list -keystore $keystore -alias $Alias -storepass:env TC_KEYSTORE_PASSWORD | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Keystore-ul generat nu a putut fi verificat.' }

    # Verificare base64 dus-intors: CI-ul primeste exact acesti octeti.
    $base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($keystore))
    $roundTrip = [Convert]::FromBase64String($base64)
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$roundTrip, [IO.File]::ReadAllBytes($keystore))) { throw 'Verificarea base64 a esuat.' }
    Write-Host "Keystore creat si verificat: $keystore" -ForegroundColor Green

    if ($SkipUpload) {
        Write-Host 'SkipUpload: nu am incarcat nimic in GitHub.'
    }
    else {
        # gh citeste valoarea din stdin cand stdin e redirectat; cmd copiaza fisierul octet cu octet,
        # iar fisierul e scris fara BOM si fara newline final.
        $temp = [IO.Path]::GetTempFileName()
        $secrets = [ordered]@{
            ANDROID_KEYSTORE_BASE64   = $base64
            ANDROID_KEYSTORE_PASSWORD = $password
            ANDROID_KEY_PASSWORD      = $password
            ANDROID_KEY_ALIAS         = $Alias
        }
        foreach ($name in $secrets.Keys) {
            [IO.File]::WriteAllText($temp, $secrets[$name], (New-Object Text.UTF8Encoding $false))
            cmd /c "gh secret set $name --repo $Repo < `"$temp`""
            if ($LASTEXITCODE -ne 0) { throw "gh secret set $name a esuat." }
        }
        Write-Host "Secretele ANDROID_* au fost actualizate in $Repo." -ForegroundColor Green
    }
}
finally {
    if ($temp) { Remove-Item -Force -ErrorAction SilentlyContinue $temp }
    Remove-Item Env:TC_KEYSTORE_PASSWORD -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Salveaza ACUM in managerul de parole, impreuna cu fisierul keystore:' -ForegroundColor Yellow
Write-Host "  Parola keystore (si a cheii): $password"
Write-Host "  Alias: $Alias"
Write-Host "  Fisier: $keystore"
Write-Host 'Apoi inchide acest terminal (parola ramane in istoricul ferestrei).'
