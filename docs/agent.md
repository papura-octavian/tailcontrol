# Agentul Windows

Agentul ruleaza pe fiecare dispozitiv controlat (PC, laptop) si este format din doua componente:

| Componenta | Unde ruleaza | Ce face |
|---|---|---|
| `TailControl.Agent.exe` | Windows Service `TailControlAgent`, cont **LocalService**, Session 0 | API HTTPS pe portul 8444, doar pe adresa Tailscale. Executa oprirea, anularea opririi si sleep-ul. |
| `TailControl.LockHelper.exe` | In sesiunea fiecarui utilizator logat (pornit la logare) | Blocheaza sesiunea (`LockWorkStation`) cand serviciul i-o cere |

## API

Toate cererile trebuie semnate (vezi [protocol-semnare.md](protocol-semnare.md)). Nu exista niciun endpoint public.

| Cerere | Efect |
|---|---|
| `POST /v1/commands/shutdown` | `shutdown.exe /s /t 10`. Aplicatiile deschise sunt inchise fortat. |
| `POST /v1/commands/cancel-shutdown` | `shutdown.exe /a` |
| `POST /v1/commands/sleep` | Raspunde imediat, apoi dupa 3 s apeleaza `SetSuspendState` (nu `rundll32`). |
| `POST /v1/commands/lock` | Cere blocarea prin helper. Raspunde `409` daca nu e niciun utilizator logat. |
| `GET /v1/status` | Versiune, nume masina, DryRun, oprire programata, numar de sesiuni cu helper |

Orice alt nume de comanda primeste `404` si nu ajunge la codul care executa. Body-ul cererii e ignorat de comenzi.

## Zero executie arbitrara

- Clientul trimite doar un nume din lista fixa (`AgentCommands`), mapat exact la un `enum`.
- Singurul proces extern este `%SystemRoot%\System32\shutdown.exe`. Calea e absoluta, iar argumentele sunt constante si trimise prin `ArgumentList`. Intarzierea vine din configurarea locala (5-600 s).
- `ProcessRunner` e singurul loc care apeleaza `Process.Start`. Refuza shell-ul, caile relative, argumentele date ca string si orice alt executabil.
- Testele `NoArbitraryExecutionTests` verifica si codul sursa: `Process.Start` si `ProcessStartInfo` apar doar in cele doua fisiere permise, si nu exista apeluri la `cmd`, `powershell`, `rundll32` sau `UseShellExecute = true`.
- Helper-ul de blocare accepta un singur mesaj (`LOCK`) si ignora orice altceva.

### Extindere ulterioara (`commands.json`)

Restart si hibernate se adauga ca valori noi in `AgentCommand` si ca metode in `SystemCommands`/`IPowerActions`. O lista configurabila (`commands.json`, scriibila doar de admin) ar inlocui `SystemCommands` cu o tabela incarcata la pornire: nume → executabil cu cale absoluta + argumente fixe. `ProcessRunner.EnsureSafe` ar valida executabilul fata de acea tabela. Clientul ar trimite in continuare doar numele.

## Blocarea sesiunii (decizie de arhitectura)

Un serviciu ruleaza in Session 0 si nu poate bloca sesiunea interactiva a utilizatorului. Solutia aleasa:

1. Serviciul creeaza named pipe-ul `\\.\pipe\TailControl.Agent.Lock`. ACL-ul e protejat (fara mostenire):
   - SYSTEM, Administrators si contul serviciului au control total;
   - INTERACTIVE are doar citire/scriere;
   - NETWORK are acces interzis explicit.
2. `TailControl.LockHelper.exe` porneste la logare in sesiunea utilizatorului, se conecteaza la pipe si asteapta.
3. Serviciul accepta doar conexiuni din sesiuni de utilizator (`GetNamedPipeClientSessionId` != 0).
4. La comanda `lock`, serviciul trimite `LOCK` tuturor helper-elor conectate. Cu mai multi utilizatori logati (fast user switching), toate sesiunile sunt blocate.

**Daca nu e nimeni logat**, nu exista helper conectat. Agentul raspunde `409 Unavailable` cu mesajul *"Niciun utilizator logat: dispozitivul este deja la ecranul de autentificare."* Aceasta e starea pe care blocarea ar fi produs-o oricum.

**Riscuri acceptate**:
- Un proces rau-intentionat care ruleaza deja ca utilizatorul logat se poate conecta la pipe in locul helper-ului si poate raspunde `OK` fara sa blocheze. Atacatorul controleaza deja sesiunea, deci nu castiga nimic in plus; efectul e doar o confirmare falsa.
- Daca serviciul nu ruleaza, un proces local poate crea un pipe cu acelasi nume. Helper-ul s-ar conecta la el, dar singura actiune pe care o accepta este blocarea propriei sesiuni.

## Contul serviciului si fisiere

Serviciul ruleaza ca **LocalService**, nu ca LocalSystem. Are privilegiul `SeShutdownPrivilege`, pe care il activeaza doar pentru sleep. Nu are drepturi de administrator.

| Folder | Continut | Drepturi |
|---|---|---|
| `%ProgramData%\TailControl\Agent\config\` | `authorized-clients.json`, certificatul Tailscale | Administrators si SYSTEM: scriere. LocalService: **doar citire**. |
| `%ProgramData%\TailControl\Agent\logs\` | `audit.log` (JSON Lines, rotatie la 5 MB) | LocalService: modificare |

Pentru ca LocalService poate doar citi folderul `config\`, un agent compromis nu poate autoriza clienti noi si nu poate inlocui certificatul.

## Administrare (local, ca administrator)

```bash
TailControl.Agent.exe authorize-client --key <cheie publica base64> --label "Telefon"
```

```bash
TailControl.Agent.exe revoke-client --id <clientId>
```

```bash
TailControl.Agent.exe list-clients
```

Prima autorizare creeaza `config\` si ii aplica ACL-ul de mai sus. Codul e comun cu Hub-ul (`AuthorizedClientsCli`). Revocarea are efect imediat, fara repornirea serviciului.

## Configurare (`appsettings.json` langa executabil)

Un exemplu complet, cu valori fictive, e in `src/TailControl.Agent/appsettings.example.json`.

| Cheie | Implicit | Note |
|---|---|---|
| `AllowedHosts` | (gol) | **Obligatoriu in productie**: numele MagicDNS al dispozitivului. Hostul face parte din semnatura. Serviciul nu porneste cu `*` sau gol. |
| `Agent:Port` | 8444 | |
| `Agent:ListenAddresses` | `[]` | Gol = detectie automata a adreselor Tailscale (100.64.0.0/10, fd7a:115c:a1e0::/48). Orice alta adresa e refuzata. |
| `Agent:TailscaleWaitSeconds` | 300 | La boot, serviciul asteapta ca Tailscale sa primeasca adresa |
| `Agent:DryRun` | `false` | `true` in Development |
| `Agent:ShutdownDelaySeconds` | 10 | 5-600 |
| `Agent:CertificatePath` / `CertificateKeyPath` | (gol) | Fisierele PEM generate de `tailscale cert`. Sunt recitite automat la reinnoire. Fara ele, agentul ruleaza pe HTTP (fallback). |

Sectiunea `Kestrel:Endpoints` este interzisa, pentru ca ar putea deschide alte adrese.

## Rulare pentru dezvoltare (fara efecte pe masina)

```bash
dotnet run --project src/TailControl.Agent
```

Profilul de dezvoltare foloseste `DryRun=true`, asculta doar pe `127.0.0.1:8444` si tine datele in `bin\...\dev-data`. Comenzile sunt doar logate.

## Ramane de verificat la instalarea reala (etapa 5)

Testele automate nu pot verifica urmatoarele, pentru ca ar opri masina de dezvoltare:
- `shutdown.exe` si `SetSuspendState` rulate sub LocalService;
- helper-ul pornit la logare (cheie `Run` in HKLM);
- regula de firewall: 8444 permis doar din 100.64.0.0/10.

Installerul si pasii de verificare vin in etapa 5.
