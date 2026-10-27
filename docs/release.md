# Release si CI/CD

## Workflow-uri GitHub Actions

| Workflow | Cand | Ce face |
|---|---|---|
| `ci.yml` | push, PR, manual | Build pentru toata solutia (inclusiv MAUI Android + Windows) si toate testele, pe `windows-latest` |
| `release.yml` | tag `v*.*.*`, manual | Teste, apoi in paralel: pachetul Hub (linux-arm), installerul agentului, installerul clientului Windows, APK-ul semnat. La final: `SHA256SUMS.txt` si GitHub Release. Rulat manual, produce doar artefactele rularii, fara Release. |

Securitatea pipeline-ului:
- actiunile sunt fixate pe SHA si actualizate de Dependabot;
- `GITHUB_TOKEN` are doar citire, cu exceptia jobului de publicare;
- checkout-ul nu pastreaza credentialele;
- keystore-ul si parolele sunt doar in GitHub Secrets.

## Cum fac un release

Dupa ce `main` e verde in CI:

```bash
git tag v0.1.0
```

```bash
git push origin v0.1.0
```

Artefactele din Release:

| Fisier | Pentru |
|---|---|
| `tailcontrol-hub-linux-arm.tar.gz` | Raspberry Pi (vezi [hub.md](hub.md)) |
| `TailControl-Agent-Setup-<v>.exe` | PC/laptop controlat |
| `TailControl-Client-Setup-<v>.exe` | Aplicatia pe Windows |
| `TailControl-<v>.apk` | Aplicatia pe Android 11+ |
| `SHA256SUMS.txt` | Verificarea tuturor fisierelor |

Verificarea pe Linux:

```bash
sha256sum -c SHA256SUMS.txt
```

Pe Windows: `Get-FileHash <fisier> -Algorithm SHA256`.

## Keystore-ul Android (o singura data)

Keystore-ul semneaza toate versiunile viitoare ale aplicatiei Android. Daca il pierzi, actualizarile nu se mai pot instala peste versiunea existenta. **Pastreaza o copie de rezerva offline**, nu in repo.

1. Genereaza-l cu `keytool`, care vine cu JDK-ul folosit de Android SDK. Cu Visual Studio e de obicei in `C:\Program Files\Microsoft\jdk-*\bin`:
   ```bash
   keytool -genkeypair -v -keystore tailcontrol-release.keystore -alias tailcontrol -keyalg RSA -keysize 4096 -validity 10000
   ```
   Alege o parola puternica si noteaz-o in managerul de parole.
2. Pune-l in GitHub Secrets. In PowerShell, codeaza keystore-ul in base64 si trimite-l ca secret:
   ```bash
   [Convert]::ToBase64String([IO.File]::ReadAllBytes("tailcontrol-release.keystore")) | gh secret set ANDROID_KEYSTORE_BASE64
   ```
   ```bash
   gh secret set ANDROID_KEY_ALIAS --body tailcontrol
   ```
   Pentru cele doua parole, comanda le cere interactiv si nu le lasa in istoricul shell-ului:
   ```bash
   gh secret set ANDROID_KEYSTORE_PASSWORD
   ```
   ```bash
   gh secret set ANDROID_KEY_PASSWORD
   ```
3. Sterge copia de lucru a keystore-ului din folderul curent, dupa ce ai facut backup-ul.

Fara aceste secrete, jobul `client-android` se opreste cu un mesaj clar. Restul artefactelor se construiesc in continuare.

## Build local (fara CI)

Din radacina repo-ului, cu PowerShell 7 (`pwsh`):

```bash
pwsh -File deploy/hub/build-package.ps1 -Version 0.1.0
```

```bash
pwsh -File deploy/agent/build-installer.ps1 -Version 0.1.0
```

```bash
pwsh -File deploy/client-windows/build-installer.ps1 -Version 0.1.0
```

Installerele cer [Inno Setup 6](https://jrsoftware.org/isinfo.php). APK-ul cere variabilele de mediu de mai sus (sau `ANDROID_KEYSTORE_PATH` catre fisierul local):

```bash
pwsh -File deploy/client-android/build-apk.ps1 -Version 0.1.0 -VersionCode 1
```

## De ce Inno Setup si nu MSIX pentru clientul Windows

| | Inno Setup (ales) | MSIX |
|---|---|---|
| Certificat | Niciunul. Fara semnare, SmartScreen avertizeaza o data. | **Obligatoriu**: certificatul trebuie instalat ca „trusted” pe fiecare PC sau cumparat de la un CA |
| Acelasi tool ca agentul | Da: un singur mod de impachetare, un singur pas in CI | Agentul (serviciu Windows) nu se potriveste oricum cu MSIX |
| Instalare per utilizator | Da, fara administrator | Da |
| Actualizari automate / dezinstalare 100% curata | Manual: rulezi installerul nou | Da (avantajul real al MSIX) |
| SecureStorage | Functioneaza unpackaged (verificat) | Functioneaza |

Pentru un proiect personal, fara certificat de semnare de cod, Inno Setup e mai simplu si la fel de sigur. Integritatea artefactelor se verifica prin `SHA256SUMS.txt`.

## Ce nu e semnat

Installerele Windows si executabilele nu au semnatura Authenticode, pentru ca ar cere un certificat de semnare de cod cumparat. Tot de aceea apare avertismentul SmartScreen. Verifica hash-urile din `SHA256SUMS.txt` (generate in CI, din codul taguit).
