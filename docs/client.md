# Aplicatia client (Android + Windows)

Aplicatie .NET MAUI cu doua tab-uri:

- **Dispozitive**: lista cu status online/offline si ecranul fiecarui dispozitiv.
- **Setari**: adresa Hub-ului, dispozitivul curent, securitate, autorizarea acestui client.

Textele sunt in romana, fara diacritice. Tema urmeaza setarea sistemului (light/dark).

| Proiect | Continut |
|---|---|
| `src/TailControl.Client.Core` | Logica fara MAUI: setari, identitate, API semnat, regulile butoanelor. Testata automat contra Hub-ului si agentului reale (in memorie). |
| `src/TailControl.Client` | Interfata MAUI: pagini XAML, view-modele (CommunityToolkit.Mvvm), integrarea cu platforma (SecureStorage, amprenta, Windows Hello) |

## Cum functioneaza

- **Lista de dispozitive** vine de la Hub si se reimprospateaza la 10 s cat timp e vizibila. Dispozitivul pe care ruleaza aplicatia (ales in Setari) nu apare ca tinta.
- **Ecranul unui dispozitiv** se reimprospateaza la 5 s si combina statusul de la Hub cu starea agentului (DryRun, oprire programata, daca e cineva logat).

| Buton | Cand e activ | Unde merge cererea | Confirmare |
|---|---|---|---|
| Porneste | offline + MAC configurat | Hub → Wake-on-LAN | dialog |
| Opreste | online, fara oprire programata | direct la agent | dialog + **amprenta/PIN** |
| Anuleaza oprirea | oprire programata (cu numaratoare inversa) | direct la agent | — |
| Blocheaza | online | direct la agent | dialog |
| Adoarme | online | direct la agent | dialog + **amprenta/PIN** |

Fiecare cerere e semnata cu cheia acestui client (vezi [protocol-semnare.md](protocol-semnare.md)). Raspunsurile de eroare devin mesaje explicite, de exemplu:
- 401: „nu este autorizat acolo, a fost revocat, sau ceasul e decalat”;
- 409: „Niciun utilizator logat”;
- „Nu pot contacta ... Este conectat la Tailscale?”.

## Autorizarea (pairing)

1. La prima pornire, aplicatia genereaza o pereche de chei **ECDSA P-256**. Cheia privata ramane in SecureStorage:
   - pe Android, in Android Keystore;
   - pe Windows, protejata cu DPAPI pentru contul curent.
2. In **Setari → Autorizarea acestui dispozitiv** apar id-ul clientului, cheia publica si codul QR. Butoanele de acolo:
   - **Trimite cheia si comenzile**: share sheet cu cheia si ambele comenzi, de exemplu catre propriul email;
   - **Comanda Hub** / **Comanda agent**: copiaza comanda gata de rulat;
   - **Copiaza doar cheia**.
3. Pe Pi ruleaza `sudo /opt/tailcontrol-hub/TailControl.Hub authorize-client --key ... --label "..." --admin`. Pe fiecare PC/laptop, ca administrator, ruleaza `TailControl.Agent.exe authorize-client --key ... --label "..."`.
4. Pentru un telefon pierdut: `revoke-client --id <id>` pe Hub si pe agenti. Efectul e imediat.

Eticheta din comenzi e numele dispozitivului, curatat de caractere speciale (fara ghilimele, `;`, `$`), ca sa nu poata injecta nimic in shell.

## Securitate pe client

- **Cheia privata** nu e afisata, exportata sau trimisa niciodata. Daca e pierduta sau corupta, se genereaza alta, iar aplicatia avertizeaza ca e nevoie de o noua autorizare.
- **Android**:
  - `allowBackup=false`: cheia si setarile nu ajung in backup-uri;
  - traficul HTTP necriptat e interzis, cu exceptia `*.ts.net` (fallback-ul HTTP din tailnet);
  - confirmarea foloseste `BiometricPrompt` din sistem cu `BIOMETRIC_STRONG | DEVICE_CREDENTIAL` (amprenta/fata sau PIN-ul telefonului). De aici cerinta **Android 11+**.
- **Windows**: confirmarea foloseste Windows Hello (PIN, amprenta, fata), legat de fereastra aplicatiei.
- Daca dispozitivul nu are nicio metoda de deblocare configurata, aplicatia spune explicit ca pasul suplimentar a fost sarit. Confirmarea poate fi dezactivata din Setari.
- Hostul Hub-ului trebuie sa fie un nume MagicDNS (`*.ts.net`). Adresele IP sau din LAN sunt refuzate, pentru ca semnatura include hostul, iar certificatul HTTPS e emis pe nume.

## Instalare

### Android

Versiunea de release (semnata cu cheia proiectului) e `TailControl-<versiune>.apk` din GitHub Releases (vezi [release.md](release.md)). Pentru un build local de test:

APK-ul se construieste cu:

```bash
dotnet publish src/TailControl.Client -f net10.0-android -c Release -p:AndroidPackageFormat=apk -o artifacts/client-android
```

Copiezi `io.github.papuraoctavian.tailcontrol-Signed.apk` pe telefon si il instalezi; Android cere permisiunea „instalare din surse necunoscute” pentru aplicatia din care il deschizi.

Acest APK e semnat cu **cheia de debug** a masinii de build. APK-ul de release (etapa 5) va avea alta semnatura, deci inainte sa-l instalezi va trebui dezinstalata versiunea de test. La dezinstalare se sterge si cheia clientului, asa ca va trebui autorizat din nou.

Telefonul trebuie sa aiba Tailscale pornit si conectat la acelasi tailnet.

### Windows

Pentru dezvoltare:

```bash
dotnet build src/TailControl.Client -f net10.0-windows10.0.19041.0
```

Executabilul e in `src/TailControl.Client/bin/Debug/net10.0-windows10.0.19041.0/win-x64/TailControl.Client.exe`. Pentru utilizare normala: `TailControl-Client-Setup-<versiune>.exe` din Releases (Inno Setup, per utilizator, fara administrator).

## Verificat

- Teste automate: `TailControl.Client.Core` contra Hub-ului si agentului reale in memorie, plus regulile butoanelor, setarile si identitatea.
- Windows: aplicatia porneste, tema dark e corecta, cheia se genereaza si se pastreaza in SecureStorage (unpackaged), iar codul QR e afisat. Contra Hub-ului real de pe Pi, conexiunea HTTPS si rezolvarea MagicDNS functioneaza, iar un client neautorizat primeste mesajul pentru 401.
- Android: APK-ul Release se construieste (trimmed, fara avertismente). **Nu a fost inca rulat pe un telefon**.
