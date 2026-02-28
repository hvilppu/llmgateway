# LlmGateway — Tunnettuja ongelmia ja pullonkauloja

## Skaalautuuko 1 000 000 dokumentille?

Skenaario: kannassa on 1 000 000 dokumenttia ja käyttäjä kysyy
_"Mikä oli Suomen keskilämpötila vuonna 2025?"_

### Mitä tapahtuu

**Vaihe 1 — LLM generoi SQL:n**

```sql
SELECT AVG(c.content.lämpötila) FROM c
WHERE c.content.pvm >= '2025-01-01' AND c.content.pvm <= '2025-12-31'
```

Tämä on hyvä kysely — aggregaatio, ei `SELECT *`. Mutta silti...

---

### Pullonkaula 1 — Cosmos DB cross-partition full scan 🔴 Kriittisin

Nykyinen partitioavain on `/id` (`tools/seed_cosmos.py`). Tämä tarkoittaa että **jokainen dokumentti on omassa partitiossaan**. 1M dokumentilla kysely menee jokaiseen partitioon erikseen.

```
Kysely → partition 1 → partition 2 → ... → partition 1 000 000
```

- Kustannus: helposti **10 000–100 000 RU** per kysely
- Cosmos DB throttlaa `429 Too Many Requests`
- Circuit breaker nappaa 429:t ja avautuu 30 sekunniksi
- Käyttäjä saa `503 Service Unavailable`

Jos throttlausta ei tule, kysely saattaa kestää **useita minuutteja**.

---

### Pullonkaula 2 — QueryService ei rajoita tuloksia 🔴

Tämä iskee kun LLM generoi ei-aggregoivan kyselyn, esim.:

```sql
SELECT * FROM c WHERE STARTSWITH(c.content.pvm, '2025')
```

`CosmosQueryService` käy koko `FeedIterator`-silmukan läpi ilman katkaisua. 300 000 dokumentilla tulos on JSON-tiedosto joka on **satoja megatavuja**. Seurauksena:

- Palvelimen muisti loppuu (OOM)
- Tai tulos lisätään messages-listaan → seuraava GPT-4 kutsu saa giganttiset tokenit

→ `QueryService.cs`, `ExecuteQueryAsync`: ei rivirajaa eikä tuloskokorajoitusta

---

### Pullonkaula 3 — Token-räjähdys messages-listassa 🟡

Vaikka `AVG` palauttaisi vain yhden luvun, jos käyttäjä kysyy useita asioita peräkkäin samassa agenttiloopissa, messages-lista kasvaa kierros kierrokselta. GPT-4:n konteksti-ikkuna ylittyy → `context_length_exceeded` virhe → `500 Internal Server Error`.

→ `ChatEndpoints.cs`, `RunAgentLoopAsync`: messages-lista kasvaa kumulatiivisesti

---

### Pullonkaula 4 — Ei aikakatkaisua Cosmos DB -kyselylle 🟡

`TimeoutMs = 15 000 ms` koskee vain Azure OpenAI -kutsuja. Cosmos DB -kyselyllä ei ole omaa timeoutia koodissa. Jos kysely kestää 2 minuuttia, gateway odottaa — ja ASP.NET Coren oletuspyyntötimeout voi katkaista koko HTTP-yhteyden ennen kuin vastaus ehtii takaisin.

→ `QueryService.cs`: `ExecuteQueryAsync` ilman `CancellationToken`-tukea

---

### Yhteenveto

| Pullonkaula | Taso | Seuraus |
|---|---|---|
| Cross-partition full scan | Cosmos DB | 429 throttle → circuit breaker → 503 |
| QueryService ilman rivirajaa | Koodi | OOM tai token-räjähdys |
| Messages-listan kasvu | Koodi | context_length_exceeded → 500 |
| Ei Cosmos DB -timeoutia | Koodi | Hidas kutsu roikkuu loputtomiin |

---

### Oikeat korjaukset tähän skaalaan

**1. Vaihda partitioavain** — `/content/paikkakunta` jolloin per-kaupunki-kyselyt osuvat yhteen partitioon. "Suomen keskilämpötila" vaatii silti cross-partition-kyselyn, mutta 10 partitiota vs 1M on eri maailma.

**2. Lisää TOP-rajoitus QueryServiceen** — pakota `SELECT TOP 500` jos kyselyssä ei ole aggregaatiota, niin tulosjoukko pysyy hallinnassa.

**3. Pre-aggregated data** — laske kuukausi-/vuosikeskiarvot etukäteen erilliseen containeriin. "Keskilämpötila 2025" osuu yhteen dokumenttiin eikä skannaa mitään.

**4. Azure SQL tai Synapse** — jos data on puhtaasti strukturoitua numeerista dataa tässä mittakaavassa, relaatiotietokanta indekseillä on oikea työkalu. Cosmos DB on optimoitu pistekyselyihin, ei analyyttisiin aggregaatioihin.
