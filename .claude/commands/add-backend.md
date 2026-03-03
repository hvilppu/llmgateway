Lisää uusi query-backend LlmGatewayhin. Uusi backend tarkoittaa uutta tietokantaa jota LLM voi kysellä agenttiloopissa.

## Vaihe 1 — Kysy käyttäjältä

Kysy seuraavat jos niitä ei ole annettu:
1. Backendin avain (esim. `postgresql`) — käytetään keyed DI:ssä ja PolicyConfigissa
2. Tietokantateknologia (PostgreSQL, MongoDB, tms.)
3. SQL-murre tai kyselysyntaksi
4. Yhteysasetukset: mitä kenttiä connection string -osioon tarvitaan
5. Pitääkö luoda uusi policy vai riittääkö lisätä QueryBackend olemassa olevaan?

## Vaihe 2 — Lue nykytila

Lue ennen muutosten tekemistä:
- `Services/QueryService.cs` — `IQueryService`, `CosmosQueryService`, `SqlQueryService`
- `Services/SchemaService.cs` — `ISchemaProvider`, `CosmosSchemaProvider`, `SqlSchemaProvider`
- `Program.cs` — keyed DI -rekisteröinnit
- `Endpoints/ChatEndpoints.cs` — miten backend valitaan (`GetQueryBackend`) ja system prompt rakennetaan

## Vaihe 3 — Tee muutokset järjestyksessä

### 1. `Services/QueryService.cs`
Lisää:
- `XxxOptions`-luokka yhteysasetuksille
- `XxxQueryService : IQueryService` -toteutus
  - `ExecuteQueryAsync`: vain SELECT sallittu (tarkistus kuten muissakin)
  - Käytä oikeaa ajuria (NuGet-paketti tarvittaessa)
  - CommandTimeout tai vastaava

### 2. `Services/SchemaService.cs`
Lisää:
- `XxxSchemaProvider : ISchemaProvider` -toteutus
  - `GetSchemaAsync`: hae skeema tietokannasta
  - `SemaphoreSlim` double-check -välimuisti, 60 min TTL
  - Virhe → palauta tyhjä string (ei kaada pyyntöä)

### 3. `appsettings.json`
Lisää uusi konfiguraatio-osio yhteysasetuksille.

### 4. `Program.cs`
Rekisteröi keyed serviceiksi:
```csharp
builder.Services.Configure<XxxOptions>(builder.Configuration.GetSection("Xxx"));
builder.Services.AddKeyedSingleton<IQueryService, XxxQueryService>("xxx");
builder.Services.AddKeyedSingleton<ISchemaProvider, XxxSchemaProvider>("xxx");
```

### 5. `Endpoints/ChatEndpoints.cs`
- Injektoi uudet keyed servicet `MapChatEndpoints`-metodiin
- Lisää `BuildXxxSystemPrompt(schema)` -metodi uuden backendin SQL-syntaksille
- Lisää haara backendin valintalogiikkaan (`GetQueryBackend` palauttaa `"xxx"`)
- Lisää uusi tool-kuvaus (`XxxQueryDatabaseTool`) syntaksikohtaisine rajoituksineen

### 6. `CLAUDE.md`
Päivitä:
- Rakenne-osio (uudet luokat)
- Query-backendit -osio (uusi backend)
- Konfiguraatio-osio (uusi appsettings-osio)
- Namespacet-taulu jos tarpeen

### 7. `TERMS.md`
Lisää uusi backend Tietokannat-osioon (syntaksi, rajoitukset, IQueryService-avain).

### 8. `architecture.mmd`
Lisää uusi ulkoinen tietokantasolmu ja yhteydet.

## Vaihe 4 — Tarkista

- Onko SELECT-tarkistus toteutettu uudessa QueryServicessä?
- Toimiiko skeemahaku ilman yhteyttä (palauttaa tyhjän, ei kaada)?
- Onko uusi policy lisätty vai riittääkö olemassa oleva?

Raportoi mitä muutit ja listaa tarvittavat NuGet-paketit.
