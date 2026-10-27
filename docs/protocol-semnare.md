# Protocolul de semnare a cererilor (TC1)

Fiecare cerere HTTP catre un agent sau catre Hub este semnata de client. Pentru o cerere nesemnata sau invalida, serverul raspunde `401` fara corp, iar motivul apare doar in log-ul serverului.

## Identitate

- Fiecare client (telefon, laptop, PC) are propria pereche de chei **ECDSA P-256**. Cheia privata ramane pe dispozitiv, in SecureStorage.
- Cheia publica e exportata ca **SPKI, base64**. Aceasta se afiseaza la pairing.
- `ClientId` = primii 16 octeti din `SHA-256(SPKI)`, in hex cu litere mici (32 de caractere). Id-ul e derivat din cheie, deci nu poate fi ales de client.

## Headere

| Header | Continut |
|---|---|
| `X-Client-Id` | `ClientId` (32 hex) |
| `X-Timestamp` | secunde Unix (doar cifre) |
| `X-Nonce` | 16 octeti aleatori, base64url fara padding (22 de caractere) |
| `X-Signature` | semnatura ECDSA P-256 / SHA-256, format IEEE P1363 (64 octeti), base64 |

Daca unul dintre aceste headere apare de mai multe ori, cererea e tratata ca nesemnata.

## Sirul semnat

Campurile sunt in UTF-8, separate prin `\n`:

```
TC1
METODA                 (litere mari)
host[:port]            (headerul Host, litere mici)
path?query             (exact cum a fost trimis, inainte de decodare)
timestamp
nonce
hex(SHA-256(body))     (body gol -> hash-ul sirului gol)
```

**Hostul** nu era cerut in specificatia initiala, dar e inclus intentionat. Fiecare server are propriul cache de nonce. Fara host, o cerere `shutdown` semnata pentru PC ar putea fi retrimisa catre laptop in fereastra de 30 de secunde. Pentru ca legatura sa conteze, fiecare server accepta doar propriile nume de host (`AllowedHosts`).

Niciun camp nu poate contine `\n`. Altfel, doua cereri diferite ar putea produce acelasi sir semnat.

## Verificare pe server

Pasii sunt in ordine. Primul care esueaza opreste verificarea:

1. Daca adresa IP e blocata temporar (vezi mai jos), raspunsul e `429`.
2. Daca body-ul depaseste limita (implicit 64 KB), raspunsul e `413`.
3. Toate headerele trebuie sa existe si sa aiba formatul exact.
4. Clientul trebuie sa existe in lista de clienti autorizati si sa nu fie revocat.
5. `|acum - timestamp| <= 30 s`.
6. Timestamp-ul nu poate fi anterior pornirii serverului. Cache-ul de nonce e in memorie si se goleste la repornire.
7. Semnatura trebuie sa fie valida pentru cheia publica a clientului.
8. Nonce-ul trebuie sa nu fi fost folosit de acelasi client. Se inregistreaza abia **dupa** pasul 7, ca cineva fara cheie sa nu poata umple cache-ul.

Fiecare esec la pasii 3-8 creste un contor pentru IP-ul respectiv. Dupa 5 esecuri intr-un minut, IP-ul e blocat 5 minute, inclusiv pentru cererile valide.

## Lista de clienti autorizati

Lista e un fisier JSON, scriibil doar de administrator, modificat doar prin comenzi locale (`authorize-client` / `revoke-client`). Nu exista un endpoint prin care un client sa se autorizeze singur.

- O intrare invalida (cheie gresita, id care nu corespunde cheii, duplicat, camp necunoscut) invalideaza tot fisierul. Daca fisierul lipseste sau e invalid, nu e autorizat nimeni.
- Fisierul e recitit automat cand se schimba, deci o revocare are efect imediat, fara repornire.
- Pe Hub, flag-ul `isAdmin` permite modificarea inventarului.

## Implementare

- `src/TailControl.Shared/Security`: semnare (`RequestSigner`, `SigningHttpHandler`), verificare (`RequestVerifier`), cache-ul de nonce si lista de clienti.
- `src/TailControl.Shared.AspNetCore`: middleware-ul folosit de Agent si de Hub, plus backoff-ul pe IP.
- `tests/TailControl.Tests`: teste pentru fiecare tip de respingere de mai sus.
