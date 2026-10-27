# Etapa 0: smoke test SQLite pe Raspberry Pi

Scop: sa confirmam ca **EF Core + SQLite** (biblioteca nativa `libe_sqlite3.so` din `SQLitePCLRaw`) merge pe Pi 3B (`armv7l`, Raspbian 12 bookworm, 32 biti) intr-o aplicatie .NET 10 self-contained, si sa vedem cata memorie consuma.

Ca a pornit ASP.NET Core pe Pi stim deja. Aici testam doar partea de SQLite.

## Ce face aplicatia

`GET /health` face urmatoarele la fiecare apel:

1. insereaza un rand in tabela `Pings`;
2. il citeste inapoi si il compara cu ce a scris;
3. numara randurile;
4. citeste `sqlite_version()`;
5. raspunde cu JSON, de exemplu:

```json
{
  "status": "ok",
  "rows": 2,
  "lastId": 2,
  "sqliteVersion": "3.53.3",
  "nativeLibrary": "e_sqlite3",
  "runtime": ".NET 10.0.12",
  "rid": "linux-arm",
  "arch": "Arm",
  "workingSetMb": 60.0,
  "elapsedMs": 40,
  "dbPath": "/home/<user>/sqlite-smoke/smoke.db"
}
```

Setarile de memorie sunt cele pe care le va folosi si Hub-ul: GC de tip workstation, fara GC concurent.

## 1. Build (pe Windows, in radacina repo-ului)

```bash
dotnet publish tools/SqliteSmoke -c Release -r linux-arm --self-contained -p:InvariantGlobalization=true -o artifacts/sqlite-smoke-linux-arm
```

```bash
tar -czf artifacts/sqlite-smoke-linux-arm.tar.gz -C artifacts sqlite-smoke-linux-arm
```

Folderul `artifacts/` e ignorat de git.

## 2. Copiere pe Pi

Inlocuieste `<user>@<pi>` cu utilizatorul si adresa Pi-ului tau:

```bash
scp artifacts/sqlite-smoke-linux-arm.tar.gz <user>@<pi>:~/
```

## 3. Pe Pi (prin ssh)

```bash
cd ~ && rm -rf sqlite-smoke-linux-arm && tar xzf sqlite-smoke-linux-arm.tar.gz && cd sqlite-smoke-linux-arm
```

```bash
chmod +x SqliteSmoke
```

Optional: verifica dependentele bibliotecii native. Nu trebuie sa apara `not found`.

```bash
ldd ./libe_sqlite3.so
```

Porneste aplicatia in fundal, ascultand doar pe loopback. Baza de date se creeaza langa executabil.

```bash
./SqliteSmoke --urls=http://127.0.0.1:8443 --Db=./smoke.db > smoke.log 2>&1 &
```

## 4. Test

Ruleaza de 2-3 ori. `rows` trebuie sa creasca la fiecare apel, iar `status` trebuie sa fie `ok`:

```bash
curl -s http://127.0.0.1:8443/health; echo
```

## 5. Masurarea memoriei

RSS curent si varful de memorie (`VmHWM`), in kB:

```bash
grep -E 'VmRSS|VmHWM' /proc/$(pgrep -f SqliteSmoke)/status
```

Confirmarea ca `libe_sqlite3.so` e chiar incarcata in proces:

```bash
grep -c e_sqlite3 /proc/$(pgrep -f SqliteSmoke)/maps
```

Memoria libera pe Pi cu aplicatia pornita:

```bash
free -m
```

## 6. Persistenta dupa repornire

```bash
pkill -f SqliteSmoke
```

```bash
./SqliteSmoke --urls=http://127.0.0.1:8443 --Db=./smoke.db > smoke.log 2>&1 &
```

Valoarea `rows` trebuie sa continue de unde a ramas:

```bash
curl -s http://127.0.0.1:8443/health; echo
```

## 7. Curatenie

```bash
pkill -f SqliteSmoke; cd ~ && rm -rf sqlite-smoke-linux-arm sqlite-smoke-linux-arm.tar.gz
```

## Ce sa imi trimiti

- iesirea de la `curl` (cel putin un apel inainte si unul dupa repornire);
- valorile `VmRSS` si `VmHWM`, plus `free -m`;
- iesirea de la `ldd ./libe_sqlite3.so`;
- daca ceva nu merge, si continutul fisierului `smoke.log`.

## Daca biblioteca nativa nu merge pe ARM32

Simptomul tipic este `DllNotFoundException: e_sqlite3` sau o eroare de incarcare a `libe_sqlite3.so` in `smoke.log`. Alternativele, in ordinea preferintei:

1. **SQLite-ul sistemului**: `sudo apt install libsqlite3-0` si pachetul `SQLitePCLRaw.bundle_sqlite3` in locul bundle-ului implicit `e_sqlite3`. Aplicatia ar folosi biblioteca din Debian, care primeste si actualizarile de securitate prin `apt`.
2. **Fara SQLite**: un fisier JSON cu scriere atomica (scris in fisier temporar, apoi `rename`) pentru inventar, si un fisier text append-only pentru audit. Volumul de date al Hub-ului este foarte mic.
