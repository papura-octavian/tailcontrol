# Politica de securitate

TailControl controleaza alimentarea unor calculatoare, deci securitatea e cerinta principala, nu un detaliu. Modelul de amenintare complet e in [docs/threat-model.md](docs/threat-model.md).

## Raportarea unei vulnerabilitati

**Nu deschide un issue public.** Foloseste raportarea privata din GitHub: tab-ul *Security* → *Report a vulnerability*.

Include:
- versiunea sau commit-ul;
- componenta (Agent, Hub, aplicatie);
- pasii de reproducere;
- impactul estimat.

Primesti un raspuns in cel mult 7 zile.

## Ce intra in scop

- Orice cale prin care o cerere ar putea executa altceva decat comenzile din lista fixa.
- Ocolirea semnaturii, a protectiei anti-replay, a revocarii sau a verificarii hostului.
- Accesul la agent sau la Hub din afara interfetei Tailscale.
- Escaladarea de privilegii prin serviciul agentului, helper-ul de blocare sau Hub.
- Scurgerea cheilor private sau a altor secrete (inclusiv in log-uri sau in repo).

## Ce nu intra in scop

- Riscurile acceptate explicit in modelul de amenintare.
- Atacuri care presupun deja administrator/root pe dispozitivul controlat sau acces la contul Tailscale al proprietarului.

## Secrete

Repo-ul nu contine:
- chei private, keystore-uri sau certificate;
- parole sau tokeni;
- MAC-uri, IP-uri sau nume reale de masini.

Configurarile de exemplu folosesc valori fictive. Keystore-ul Android si parolele lui exista doar in GitHub Secrets.
