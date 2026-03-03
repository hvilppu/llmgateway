Lisää uusi policy LlmGatewayhin. Kysy ensin tarvittavat tiedot, sitten tee muutokset.

## Vaihe 1 — Kysy käyttäjältä

Kysy seuraavat jos niitä ei ole annettu:
1. Policyn nimi (esim. `tools_premium`)
2. PrimaryModel (`gpt4` vai `gpt4oMini`)
3. ToolsEnabled (`true` / `false`)
4. QueryBackend (`cosmos` / `mssql`) — vain jos ToolsEnabled=true
5. RagEnabled (`true` / `false`)
6. Fallback-mallit (valinnainen, esim. `["gpt4oMini"]`)

## Vaihe 2 — Lue nykytila

Lue ennen muutosten tekemistä:
- `appsettings.json` — Policies-osio
- `Routing/Routing.cs` — PolicyConfig-rakenne ja mahdolliset validoinnit
- `CLAUDE.md` — Policy-pohjainen routing -osio

## Vaihe 3 — Tee muutokset järjestyksessä

### 1. `appsettings.json`
Lisää uusi policy Policies-osioon oikean rakenteen mukaan:
```json
"uusi_policy": {
  "PrimaryModel": "gpt4",
  "ToolsEnabled": true,
  "QueryBackend": "cosmos",
  "RagEnabled": false,
  "Fallbacks": ["gpt4oMini"]
}
```

### 2. `CLAUDE.md`
Lisää policy Policy-pohjainen routing -osioon kuvauksella.

### 3. `TERMS.md`
Lisää policy Policy-tauluun jos siellä on sellainen. Tarkista onko taulukko olemassa.

### 4. `architecture.mmd`
Lisää uusi reitityshaara RoutingEngine-solmusta jos polku eroaa olemassa olevista.

## Vaihe 4 — Tarkista

- Onko policy käytettävissä `tools`-polussa? Jos kyllä, tarvitaanko sille oma system prompt `ChatEndpoints.cs`:ssä?
- Jos RagEnabled=true: toimii automaattisesti, erillisiä muutoksia ei tarvita
- Jos uusi QueryBackend: tarvitaan uusi keyed service `Program.cs`:ssä

Raportoi mitä muutit ja mitä jäi käyttäjän tehtäväksi.
