# TailControl

Control la distanta al propriilor dispozitive (pornire prin Wake-on-LAN, oprire, blocare, sleep, status) dintr-o aplicatie .NET MAUI pe Android si Windows, peste Tailscale, cu un Raspberry Pi ca hub.

> Proiect in lucru. Documentatia completa (arhitectura, model de amenintare, checklist de instalare) se adauga pe etape.

## Componente

| Proiect | Rol |
|---|---|
| `src/TailControl.Shared` | Contracte (DTO), semnarea/verificarea cererilor, constante |
| `src/TailControl.Shared.AspNetCore` | Middleware de verificare a semnaturilor, comun pentru Agent si Hub |
| `src/TailControl.Agent` | Serviciu Windows pe fiecare dispozitiv controlat (port 8444) |
| `src/TailControl.Hub` | API pe Raspberry Pi: inventar, Wake-on-LAN, status (port 8443) |
| `src/TailControl.Client` | Aplicatia .NET MAUI (Android + Windows) |
| `tests/TailControl.Tests` | Teste automate |
| `tools/` | Utilitare si teste de fezabilitate (ex. smoke test SQLite pe Pi) |

## Documentatie

- [Protocolul de semnare a cererilor](docs/protocol-semnare.md)

## Build

```bash
dotnet build TailControl.slnx
dotnet test TailControl.slnx
```
