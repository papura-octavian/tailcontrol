# Hub-ul (Raspberry Pi)

Hub-ul ruleaza pe Raspberry Pi 3B (Raspbian 12, armv7l), pe langa Pi-hole. Are trei roluri:

- tine **inventarul** dispozitivelor;
- trimite **Wake-on-LAN** in LAN;
- verifica la fiecare 15 s daca agentii sunt **online**.

Comenzile de oprire, blocare si sleep **nu** trec prin Hub: clientul le trimite direct agentului.

## API

Toate cererile trebuie semnate (vezi [protocol-semnare.md](protocol-semnare.md)).

| Cerere | Cine | Efect |
|---|---|---|
| `GET /v1/devices` | orice client autorizat | Lista cu `online`, `lastSeenUtc`, `lastCheckedUtc` |
| `GET /v1/devices/{id}` | orice client autorizat | Un dispozitiv |
| `POST /v1/devices` | **admin** | Adaugare (validata, normalizata) |
| `PUT /v1/devices/{id}` | **admin** | Modificare |
| `DELETE /v1/devices/{id}` | **admin** | Stergere |
| `POST /v1/devices/{id}/wake` | orice client autorizat | Magic packet catre MAC-ul **din inventar** |
| `GET /v1/audit?take=50` | **admin** | Ultimele intrari de audit |

- Un client e admin daca a fost autorizat cu `--admin`.
- Un client fara drept de admin care incearca o modificare primeste `403`, iar incercarea apare in audit.
- Cererile de wake nu primesc nicio adresa de la client: Hub-ul trimite pachetul doar catre MAC-ul salvat in inventar.

### Validare

| Camp | Reguli |
|---|---|
| Nume | 1-40 caractere, fara caractere de control, unic (nu conteaza literele mari/mici) |
| Host | Numele MagicDNS complet, terminat in `.ts.net` (configurabil prin `Hub:AllowedDeviceHostSuffixes`). Adresele IP si numele scurte sunt respinse. Hub-ul nu verifica si clientul nu contacteaza nimic in afara tailnet-ului. |
| MAC | `AA:BB:CC:DD:EE:FF`, `aa-bb-...` sau `aabbcc...`, normalizat la prima forma. Adresele multicast, broadcast si zero sunt respinse. Optional: fara MAC, wake raspunde `409`. |
| JSON | Campurile necunoscute, lipsa sau `null` acolo unde nu e permis duc la `400` |

## Status online/offline

- La fiecare 15 s, Hub-ul face un **TCP connect** la `host:8444` pentru fiecare agent. Nu trimite nimic si nu se autentifica, iar agentul nu raspunde nimic fara semnatura.
- Rezultatul e tinut in memorie. `lastSeenUtc` e scris in baza de date cel mult o data la 10 minute per dispozitiv, ca sa nu uzeze cardul SD.
- **Tailscale ACL**: Pi-ul (`tag:hub`) trebuie sa ajunga la `tag:agent:8444`, altfel toti agentii apar offline.

## Wake-on-LAN

- Pachetul are 102 octeti (6 × `0xFF` + 16 × MAC). E trimis de 3 ori prin UDP broadcast pe portul 9, catre `Hub:WakeBroadcastAddress`.
- Adresa de broadcast poate fi doar un broadcast de LAN privat (ex. `192.168.1.255`) sau `255.255.255.255`. Altfel serviciul nu porneste.
- Daca adresa nu e setata, wake raspunde `503`.

## Unde stau lucrurile pe Pi

| Cale | Proprietar / drepturi | Continut |
|---|---|---|
| `/opt/tailcontrol-hub/` | root, doar citire pentru serviciu | Aplicatia (self-contained, ReadyToRun) |
| `/etc/tailcontrol/hub/` | `root:tailcontrol-hub` 2750 | `hub.json`, `authorized-clients.json` (0640), `certs/`, `cert.env` (0600, doar root) |
| `/var/lib/tailcontrol-hub/hub.db` | `tailcontrol-hub` 0700 | SQLite: inventar + audit (ultimele 10 000 de intrari) |

Serviciul (`tailcontrol-hub.service`) ruleaza ca utilizatorul de sistem `tailcontrol-hub`, fara capabilitati, cu `ProtectSystem=strict`, `NoNewPrivileges`, `PrivateTmp`, filtru de apeluri de sistem si `MemoryMax=300M`. Poate scrie doar in `/var/lib/tailcontrol-hub`. Lista de clienti autorizati poate fi modificata doar de root, deci un Hub compromis nu isi poate adauga clienti.

## Instalare pe Pi

Pe Windows, din radacina repo-ului:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/hub/build-package.ps1
```

```bash
scp artifacts/tailcontrol-hub-linux-arm.tar.gz <user>@<pi>:~/
```

Pe Pi:

```bash
tar xzf tailcontrol-hub-linux-arm.tar.gz && cd tailcontrol-hub-linux-arm && sudo sh install.sh
```

Scriptul nu porneste serviciul cat timp configurarea are valori de exemplu. Pasii urmatori:

1. `sudo nano /etc/tailcontrol/hub/hub.json`: seteaza `AllowedHosts` la numele MagicDNS al Pi-ului si `Hub:WakeBroadcastAddress` la broadcast-ul LAN-ului.
2. `sudo nano /etc/tailcontrol/hub/cert.env`: seteaza `CERT_DOMAIN` la acelasi nume MagicDNS.
3. Obtine certificatul. Trebuie ca **HTTPS** sa fie activat in consola Tailscale (DNS → HTTPS Certificates):

   ```bash
   sudo systemctl start tailcontrol-hub-cert.service
   ```

   Daca nu vrei inca certificat, sterge `CertificatePath` si `CertificateKeyPath` din `hub.json`. Hub-ul va rula pe HTTP in tailnet (fallback).
4. Porneste serviciul:

   ```bash
   sudo systemctl start tailcontrol-hub.service
   ```

5. Autorizeaza clientii (cheia publica e afisata de aplicatie):

   ```bash
   sudo /opt/tailcontrol-hub/TailControl.Hub authorize-client --key <cheie> --label "Telefon" --admin
   ```

Actualizarea se face la fel: noul pachet si `sudo sh install.sh`. Configurarea si baza de date raman. Pentru dezinstalare: `sudo sh uninstall.sh` (sau `--purge`).

### Verificare

```bash
systemctl status tailcontrol-hub --no-pager
```

```bash
journalctl -u tailcontrol-hub -n 30 --no-pager
```

Hub-ul trebuie sa asculte **doar** pe adresa Tailscale (100.x), nu pe `0.0.0.0` sau pe IP-ul din LAN:

```bash
sudo ss -ltnp | grep 8443
```

Fara semnatura, raspunsul trebuie sa fie `401`:

```bash
curl -sk -o /dev/null -w "%{http_code}\n" https://<nume-pi>.<tailnet>.ts.net:8443/v1/devices
```

Memoria folosita de serviciu:

```bash
systemctl show tailcontrol-hub -p MemoryCurrent
```

Scorul de hardening systemd (mai mic = mai bine):

```bash
systemd-analyze security tailcontrol-hub --no-pager | tail -1
```

### DNS: Pi-hole si MagicDNS

Hub-ul contacteaza agentii prin numele `*.ts.net`. Daca Pi-ul foloseste Pi-hole ca DNS (adica Tailscale e pornit cu `--accept-dns=false`), aceste nume s-ar putea sa nu se rezolve pe Pi. Verifica:

```bash
getent hosts <pc>.<tailnet>.ts.net
```

Daca nu intoarce nimic, adauga in Pi-hole o redirectionare a domeniului `ts.net` catre resolver-ul Tailscale `100.100.100.100`. Pe Pi-hole v5 se face cu fisierul `/etc/dnsmasq.d/05-tailscale.conf` care contine `server=/ts.net/100.100.100.100`. Pe Pi-hole v6 se face din Settings → All settings → `misc.dnsmasq_lines`. Este un pas manual (vezi setup-checklist).

### Firewall (optional, aparare in adancime)

Hub-ul asculta oricum doar pe adresa Tailscale. Daca vrei si o regula explicita care permite portul 8443 doar din 100.64.0.0/10, o adaugi manual (nftables/iptables), avand grija sa nu afectezi regulile Pi-hole.

## Dezvoltare (pe Windows)

```bash
dotnet run --project src/TailControl.Hub
```

In Development, Hub-ul asculta pe `127.0.0.1:8443`, iar datele stau in `bin/.../dev-data`. Migrarile se genereaza cu tool-ul local `dotnet-ef` (versiune fixata in `dotnet-tools.json`):

```bash
dotnet tool restore
```

```bash
dotnet ef migrations add <Nume> --project src/TailControl.Hub --output-dir Data/Migrations
```
