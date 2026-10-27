# Model de amenintare

TailControl poate opri, bloca, adormi si porni calculatoare. Documentul descrie ce protejam, de cine si cum, plus riscurile ramase in mod constient.

## Ce protejam

| Bun | De ce conteaza |
|---|---|
| Disponibilitatea si datele de pe PC/laptop | Oprirea fortata inchide aplicatiile fara salvare |
| Integritatea PC-urilor | Un sistem de control la distanta e tinta ideala pentru executie de cod |
| Cheile private ale clientilor | Cine are cheia poate trimite comenzi |
| Lista de clienti autorizati | Cine o poate modifica isi poate da singur acces |
| Inventarul (nume, MAC-uri, nume MagicDNS) | Informatie despre retea, plus tinta pentru Wake-on-LAN |
| Pi-ul (Pi-hole + Hub) | DNS-ul retelei ruleaza pe el |

## Cine ar putea ataca

| Actor | Ce poate face, in principiu |
|---|---|
| **A1. Internet** | Nimic: nu exista porturi expuse public (fara Funnel, fara port forwarding) |
| **A2. Cineva din LAN** (Wi-Fi-ul casei, un musafir) | Scaneaza si trimite pachete in LAN |
| **A3. Un dispozitiv din tailnet care nu e al meu** (device partajat de alt cont) sau un dispozitiv al meu compromis | Trimite trafic prin Tailscale |
| **A4. Cine fura telefonul** (deblocat sau nu) | Are aplicatia si, cu deblocarea, cheia |
| **A5. Un observator pe drum** (Wi-Fi public, operator) | Vede si modifica trafic |
| **A6. Malware pe PC-ul cu agent**, cu drepturi de utilizator | Ruleaza local, ca utilizatorul logat |
| **A7. Un Hub (Pi) compromis** | Are procesul Hub-ului si baza lui de date |
| **A8. Dependinte compromise** (supply chain) | Cod rau-intentionat intr-un pachet |

## Atenuari

### Retea (A1, A2, A3)

- Agentul si Hub-ul asculta **doar pe adresele Tailscale** (100.64.0.0/10, fd7a:115c:a1e0::/48). La pornire refuza `0.0.0.0`, adresele din LAN si sectiunea `Kestrel:Endpoints` din configurare. Verificat pe Pi cu `ss -ltnp`.
- Pe Windows, regula de firewall permite TCP 8444 doar din 100.64.0.0/10 si doar pentru `TailControl.Agent.exe`.
- **ACL-urile Tailscale cu tag-uri** (vezi [tailscale-acl.example.hujson](tailscale-acl.example.hujson)):
  - doar `tag:client` ajunge la agenti (8444) si la Hub (8443);
  - doar `tag:hub` ajunge la agenti, pentru verificarea statusului;
  - un device partajat din alt cont nu are niciun drum;
  - sectiunea `tests` din politica verifica asta la fiecare salvare.
- Chiar daca ACL-ul ar fi gresit, A3 se loveste de urmatorul strat: **fiecare cerere trebuie semnata** de un client autorizat.

### Autentificare (A3, A5)

- Fiecare client are o pereche de chei **ECDSA P-256**. Fiecare cerere e semnata peste metoda, host, path, query, timestamp, nonce si SHA-256(body). Detalii in [protocol-semnare.md](protocol-semnare.md).
- **Anti-replay**:
  - fereastra de ±30 s;
  - nonce unic per client, inregistrat abia dupa ce semnatura e valida;
  - cererile semnate inainte de pornirea serverului sunt respinse, pentru ca la repornire cache-ul de nonce se goleste.
- **Hostul e semnat**: o cerere pentru PC nu poate fi retrimisa catre laptop. Fiecare server accepta doar propriul nume (`AllowedHosts` e obligatoriu).
- Toate esecurile de autentificare primesc acelasi raspuns, `401` fara detalii. Dupa 5 esecuri intr-un minut, IP-ul e blocat 5 minute. Exista si o limita de 60 de cereri pe minut per IP, aplicata **inainte** de verificarea semnaturii.
- Transportul e WireGuard (Tailscale), plus **HTTPS cu certificatul `tailscale cert`**. Fallback-ul documentat e HTTP in tailnet, cu semnaturile in continuare obligatorii.

### Autorizare si administrare (A3, A4, A7)

- Lista de clienti autorizati se modifica **doar local, ca administrator/root** (`authorize-client`, `revoke-client`). Nu exista endpoint de inregistrare.
- **Fisierul de chei nu poate fi scris de serviciu**:
  - pe Windows: `config\` e scriibil doar de Administrators/SYSTEM, iar serviciul (LocalService) are doar citire;
  - pe Pi: `/etc/tailcontrol/hub` e root:tailcontrol-hub 2750, iar fisierul e 0640.

  Un agent sau Hub compromis nu isi poate adauga clienti noi.
- Un fisier de chei invalid sau modificat gresit nu autorizeaza pe nimeni (fail closed).
- Revocarea are efect imediat, fara repornire.
- Pe Hub, modificarea inventarului cere flag-ul `isAdmin`. Incercarile interzise apar in audit.

### Telefon furat (A4)

- Cheia privata e in Android Keystore (SecureStorage), nu poate fi exportata, si nu ajunge in backup-uri (`allowBackup=false`).
- Oprirea si sleep-ul cer amprenta sau PIN-ul telefonului, prin `BiometricPrompt` cu `BIOMETRIC_STRONG | DEVICE_CREDENTIAL`.
- Raspunsul la incident: `revoke-client --id <id>` pe Hub si pe fiecare agent. Pasii sunt in [setup-checklist.md](setup-checklist.md).

### Zero executie arbitrara

Aceasta e cea mai importanta proprietate, pentru ca ea limiteaza ce poate face oricine ajunge pana la agent.

- Clientul trimite **doar un nume din lista fixa** (`shutdown`, `lock`, `sleep`, `cancel-shutdown`), mapat exact la un `enum`. Valorile numerice sau orice alt text dau 404 inainte sa ajunga la codul care executa.
- Singurul proces extern este `%SystemRoot%\System32\shutdown.exe`, cu cale absoluta, argumente constante si `ArgumentList`. Fara shell, fara concatenare de string-uri.
- `ProcessRunner` refuza orice altceva: shell, cale relativa, argumente ca string, alt executabil.
- Sleep-ul e un apel direct `SetSuspendState`, iar lock-ul e `LockWorkStation` in helper. Helper-ul accepta un singur mesaj, `LOCK`.
- **Teste automate** verifica toate acestea, inclusiv o scanare a codului sursa: `Process.Start` si `ProcessStartInfo` apar doar in cele doua fisiere permise, si nu exista `cmd`, `powershell` sau `UseShellExecute = true` (`NoArbitraryExecutionTests`).

### Drepturi minime (A6, A7)

- **Agentul**:
  - ruleaza ca LocalService, nu SYSTEM;
  - privilegiile serviciului sunt restranse cu `sc privs` la `SeShutdownPrivilege`, `SeChangeNotifyPrivilege` si `SeCreateGlobalPrivilege`.
- **Pipe-ul de blocare**:
  - ACL protejat: utilizatorii interactivi doar citesc si scriu, iar NETWORK e refuzat explicit;
  - conexiunile din Session 0 sunt refuzate.
- **Hub-ul**:
  - utilizator de sistem dedicat, fara capabilitati;
  - restrictii systemd: `ProtectSystem=strict`, `NoNewPrivileges`, `PrivateTmp`, filtru de apeluri de sistem, familii de adrese restranse;
  - poate scrie doar in `/var/lib/tailcontrol-hub`. Scorul `systemd-analyze security` e 1.5.

### Audit

- Agentul scrie in `logs\audit.log` (JSON Lines, deci fara injectie de linii). Hub-ul scrie in tabela `AuditLog`, cu ultimele 10 000 de intrari.
- Se inregistreaza cine, ce, cand si cu ce rezultat. Nu se inregistreaza secrete: nici semnaturi, nici chei, nici nonce-uri.

### Supply chain (A8)

- Versiunile pachetelor sunt fixate central (`Directory.Packages.props`), inclusiv dependintele tranzitive. Dependabot verifica saptamanal NuGet, GitHub Actions si SDK-ul.
- Actiunile din CI sunt fixate pe SHA. `GITHUB_TOKEN` are doar citire, cu exceptia jobului de publicare. Keystore-ul Android e doar in GitHub Secrets.
- Am evitat pachetele inutile. De exemplu, pentru amprenta pe Android se foloseste API-ul de sistem, nu AndroidX.Biometric.

## Riscuri ramase (acceptate)

| Risc | De ce e acceptat / ce il limiteaza |
|---|---|
| Un telefon furat **si deblocat**, inainte de revocare | Amprenta/PIN-ul e cerut pentru oprire si sleep, dar blocarea si pornirea nu il cer. Atacatorul ar putea doar porni sau bloca PC-ul pana la revocare. |
| Malware care ruleaza ca utilizatorul logat pe PC poate impersona helper-ul de blocare | Ar putea raspunde „OK” fara sa blocheze. Controleaza deja sesiunea, deci nu castiga nimic in plus. |
| Un proces local care ocupa numele pipe-ului cat timp serviciul e oprit | Helper-ul accepta doar `LOCK`, deci efectul maxim e blocarea propriei sesiuni |
| Numele masinilor apar in jurnalele publice Certificate Transparency | Acceptat explicit pentru `tailscale cert` |
| Fereastra de replay de 30 s intre servere diferite | Inchisa de semnarea hostului si de `AllowedHosts` |
| Un Hub compromis poate minti despre status sau inventar | Hub-ul nu poate trimite comenzi agentilor (nu are cheie de client) si nu-si poate autoriza clienti. Pornirea foloseste doar MAC-urile din inventar. |
| Wake-on-LAN e neautentificat in LAN, prin natura protocolului | Oricine din LAN putea oricum sa trimita magic packet. Hub-ul doar il trimite, la cererea unui client autorizat. |
| Agentul si installerele nu sunt semnate cu certificat de cod | SmartScreen avertizeaza. Integritatea se verifica cu `SHA256SUMS.txt` din Release. |
| `MemoryMax` pe Pi nu se aplica fara `cgroup_enable=memory` | Pas manual optional, documentat |
| Ceasul clientului decalat cu peste 30 s | Cererile dau 401 cu mesaj clar. Telefoanele si PC-urile folosesc NTP. |
