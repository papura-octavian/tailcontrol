# Checklist de instalare

Toti pasii de mai jos se fac manual, pe dispozitivele tale. Proiectul nu modifica BIOS-ul, setarile Windows sau politica Tailscale. Valorile din exemple (nume, emailuri) sunt fictive.

## 0. Deja facut si verificat (referinta)

- [x] **BIOS PC**: *Power On By PCI-E* = Enabled, *ErP Ready* = Disabled.
- [x] **Windows Fast Startup** dezactivat. Cu el activ, „oprirea” e de fapt o hibernare partiala, iar placa de retea poate sa nu mai asculte magic packet-ul.
- [x] **Device Manager**, la placa de retea a PC-ului:
  - *Power Management*: „Allow this device to wake the computer” bifat;
  - *Advanced*: „Wake on Magic Packet” = Enabled.
- [x] **Test manual de pe Pi**: `wakeonlan <MAC-ul PC-ului>` porneste PC-ul, inclusiv prin repeater.
- [x] **Hub-ul pe Pi**: instalat si verificat (vezi [hub.md](hub.md)).
- [x] **Telefon**: autorizat pe Hub, iar pornirea PC-ului functioneaza pe Wi-Fi si pe date mobile.

## 1. Tailscale (consola de administrare)

- [ ] **DNS → MagicDNS** activat.
- [ ] **DNS → HTTPS Certificates** activat. Numele masinilor apar astfel in jurnalele publice Certificate Transparency (risc acceptat).
- [ ] **Sterge dispozitivele vechi** din *Machines*.
- [ ] **Tag-uri** (*Machines → … → Edit ACL tags*):
  - telefon: `tag:client`
  - PC: `tag:client`, `tag:agent`
  - laptop: `tag:client`, `tag:agent`
  - Raspberry Pi: `tag:hub`

  Dispozitivele cu tag-uri nu mai expira la re-autentificare, iar accesul lor e decis doar de tag-uri. Pune tag-urile **inainte** de pasul urmator.
- [ ] **Access controls**: inlocuieste politica implicita („toti catre toti”) cu una pornita de la [tailscale-acl.example.hujson](tailscale-acl.example.hujson):
  - adauga regulile pentru restul traficului tau (SSH la Pi, DNS, RDP);
  - pune la teste adresa reala a contului cu care e partajat celalalt dispozitiv;
  - salveaza. Tailscale ruleaza testele si refuza politica daca un test pica.
- [ ] **Verifica excluderea dispozitivului partajat**: de pe el, `curl https://<pc>.<tailnet>.ts.net:8444/v1/status` trebuie sa expire (timeout), nu sa primeasca `401`.

## 2. PC-ul (si apoi laptopul): agentul

- [ ] Descarca `TailControl-Agent-Setup-<versiune>.exe` si `SHA256SUMS.txt` din GitHub Releases. Verifica fisierul in PowerShell, comparand hash-ul cu cel din `SHA256SUMS.txt`:
  ```bash
  Get-FileHash .\TailControl-Agent-Setup-0.1.0.exe -Algorithm SHA256
  ```
- [ ] Ruleaza installerul (cere administrator). SmartScreen avertizeaza ca installerul nu e semnat; alegi *More info → Run anyway*.
  - **Numele MagicDNS** e completat automat din Tailscale. Verifica-l.
  - **HTTPS** ramane bifat (cere pasul 1).
  - **DryRun**: bifeaza-l la prima instalare, daca vrei sa testezi intai fara efecte. Apoi in `C:\ProgramData\TailControl\Agent\config\agent.json` pui `"DryRun": false` si repornesti serviciul cu `Restart-Service TailControlAgent`.
- [ ] Verificari, in PowerShell ca administrator:
  ```bash
  Get-Service TailControlAgent
  ```
  ```bash
  netstat -ano | findstr :8444
  ```
  Trebuie sa apara doar adresa `100.x` (si cea `fd7a:...`), nu `0.0.0.0`.
  ```bash
  Get-NetFirewallRule -DisplayName "TailControl Agent (Tailscale)" | Get-NetFirewallAddressFilter
  ```
  ```bash
  Get-Process TailControl.LockHelper
  ```
- [ ] **Autorizeaza clientii pe agent.** In aplicatie (Setari → **Comanda agent**) ai comanda. O rulezi in PowerShell ca administrator, din `C:\Program Files\TailControl\Agent`:
  ```bash
  .\TailControl.Agent.exe authorize-client --key <cheie> --label "Telefon"
  ```
  Repeta pentru fiecare client care trebuie sa comande acest PC: telefonul, laptopul.
- [ ] **Test pas cu pas din telefon**:
  1. **Blocheaza**: sesiunea se blocheaza.
  2. **Opreste**: apare numaratoarea de 10 s; apesi **Anuleaza oprirea**.
  3. **Adoarme**: PC-ul intra in sleep.
  4. **Porneste**: PC-ul se trezeste.
  5. **Opreste** pana la capat, apoi **Porneste**.

  Daca oprirea sau sleep-ul esueaza, verifica in Event Viewer (Application, sursa *TailControlAgent*). Cauza probabila e privilegiul de oprire al contului LocalService.

## 3. Laptopul (de facut cand vrei)

- [ ] Instaleaza agentul ca la pasul 2.
- [ ] **Wake-on-LAN nu e garantat.** Multe laptopuri nu pornesc din starea oprit, mai ales pe Wi-Fi; din sleep au mai multe sanse.
  - In BIOS/UEFI cauta *Wake on LAN* / *Power on by LAN*, daca exista.
  - In Device Manager, la placa de retea (de preferat cea pe cablu sau dock-ul): *Allow this device to wake the computer* si *Wake on Magic Packet*.
  - In aplicatie, adauga MAC-ul placii pe care o folosesti. La laptopuri, aplicatia afiseaza o nota despre aceasta limita.

## 4. Telefonul (Android 11+)

- [ ] Descarca `TailControl-<versiune>.apk` din Releases si verifica hash-ul.
- [ ] Daca ai instalata versiunea de test, semnata cu cheia de debug, **dezinstaleaz-o intai**, pentru ca semnaturile difera. Telefonul va genera o cheie noua: revoca-o pe cea veche si autorizeaza-o pe cea noua (pasul 6).
- [ ] Telefonul trebuie sa aiba blocare cu amprenta sau PIN, altfel pasul de confirmare suplimentara e sarit (aplicatia spune asta).

## 5. Aplicatia Windows (pe PC si/sau laptop)

- [ ] Ruleaza `TailControl-Client-Setup-<versiune>.exe`. Se instaleaza doar pentru utilizatorul curent, fara administrator.
- [ ] In **Setari**: adresa Hub-ului, apoi **Acest dispozitiv** (e ghicit automat dupa numele PC-ului), apoi autorizarea, ca la pasul 6.

## 6. Autorizarea unui client nou (pairing)

1. In aplicatie: **Setari → Autorizarea acestui dispozitiv → Trimite cheia si comenzile** (sau **Comanda Hub** / **Comanda agent**).
2. Pe Pi:
   ```bash
   sudo /opt/tailcontrol-hub/TailControl.Hub authorize-client --key <cheie> --label "<nume>" --admin
   ```
3. Pe fiecare PC/laptop pe care clientul trebuie sa-l comande, ca administrator:
   ```bash
   .\TailControl.Agent.exe authorize-client --key <cheie> --label "<nume>"
   ```
4. In aplicatie: **Salveaza si testeaza** trebuie sa afiseze „Conectat”.

## 7. Telefon pierdut sau furat

1. Afla id-ul clientului de pe Pi:
   ```bash
   sudo /opt/tailcontrol-hub/TailControl.Hub list-clients
   ```
2. Revoca-l pe Pi:
   ```bash
   sudo /opt/tailcontrol-hub/TailControl.Hub revoke-client --id <id>
   ```
3. Revoca-l pe fiecare PC/laptop, ca administrator:
   ```bash
   .\TailControl.Agent.exe revoke-client --id <id>
   ```
4. In consola Tailscale, sterge sau dezactiveaza telefonul.

Revocarea are efect imediat, fara repornirea serviciilor.

## 8. Verificarea finala (criteriile de „gata”)

- [ ] De pe Android: oprire, blocare si sleep pe PC-ul pornit; pornire cand e oprit; statusul se actualizeaza in cateva secunde.
- [ ] De pe aplicatia Windows (laptop): aceleasi comenzi catre PC.
- [ ] Agentul si Hub-ul nu raspund in afara Tailscale. Din LAN, `curl http://<ip-lan-pc>:8444` trebuie sa dea *connection refused* sau timeout.
- [ ] O cerere de la un client revocat primeste `401`. Testele automate acopera si restul cazurilor: semnatura gresita, timestamp vechi, nonce refolosit.
