# RAG — Retrieval-Augmented Generation

Tämä dokumentti kuvaa kuinka RAG-haku toimii LlmGateway-järjestelmässä: miten kuukausiraportit syntyvät, miten ne haetaan vektorihaulla ja miten ne yhdistyvät function calling -agenttilooppiin.

---

## Mitä RAG tekee tässä järjestelmässä

Ilman RAGia LLM vastaa kyselyihin pelkästään SQL-haun tuloksilla — se saa luvut mutta ei kontekstia. RAG lisää semanttisen kerroksen: ennen SQL-hakua järjestelmä hakee **laadullisia kuukausikuvauksia** (esim. "helmikuu 2025 Helsingissä oli poikkeuksellisen kylmä"), jotka kertovat LLM:lle mistä ajankohdasta ja tilanteesta on kyse. LLM osaa tämän perusteella muotoilla paremman vastauksen ja kohdistaa SQL-haun oikein.

**Tulos**: RAG-policy vastaa sekä laadullisiin ("millainen talvi?") että numeerisiin ("mikä oli keskilämpötila?") kysymyksiin — pelkkä SQL ei pysty ensimmäiseen.

---

## Arkkitehtuuri kahdessa osassa

```
┌─────────────────────────────────────────────────────────────────┐
│  SyncFunction                                                    │
│                                                                  │
│  Käynnistys (kerran):                                           │
│    ReportBackfillService → GenerateAllMonthsReportsAsync()      │
│      → kaikki (paikkakunta × vuosi × kuukausi) -yhdistelmät    │
│      → GPT-4o-mini + embedding → kuukausiraportit               │
│                                                                  │
│  Joka 15 min (ajastin):                                         │
│    CosmosSyncService: documents → SQL (mittaukset)              │
│    Jos uusia dokumentteja tuli:                                 │
│      → GenerateCurrentMonthReportsAsync()                       │
│      → vain kuluva kuukausi → GPT-4o-mini + embedding → upsert │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  LlmGateway (POST /api/chat, policy: "rag")                     │
│                                                                  │
│  1. Embeddaa käyttäjän kysymys (text-embedding-3-small)         │
│  2. VectorDistance-haku kuukausiraportit-containerista (TOP 3)  │
│  3. Injektoi löydetyt kuvaukset system promptiin                │
│  4. Agenttiloop: LLM generoi SQL → query_database → vastaus     │
└─────────────────────────────────────────────────────────────────┘
```

---

## Osa 1: Kuukausiraporttien generointi (SyncFunction)

Raportteja generoidaan kahdessa tilanteessa:

| Tilanne | Metodi | Mitä käy läpi |
|---|---|---|
| Käynnistys (kerran) | `GenerateAllMonthsReportsAsync` | Kaikki (paikkakunta × kuukausi) -yhdistelmät joille on dataa |
| 15 min timer, vain jos uusia dok. | `GenerateCurrentMonthReportsAsync` | Vain kuluva kuukausi |

### Miten raportti syntyy (molemmissa sama prosessi)

**Vaihe 1 — Haetaan mittaukset kyseiselle kuukaudelle**

```
SELECT * FROM c
WHERE STARTSWITH(c.content.pvm, '2025-03')
   OR STARTSWITH(c.pvm, '2025-03')
```

Tulos esim. Helsinki: 31 päivän lämpötilamittaukset.

**Vaihe 2 — GPT-4o-mini kirjoittaa kuvauksen (ei lukuja)**

System prompt kieltää luvut eksplisiittisesti:
> "Kirjoita lyhyt laadullinen kuvausteksti kuukauden säästä. **Älä mainitse lukuja, tilastoja tai asteita** — kuvaile sanoin millainen sää oli."

User-viestissä luvut ovat OK promptina — LLM saa kaiken datan mutta kirjoittaa laadullisen yhteenvedon:
```
Paikkakunta: Helsinki
Kuukausi: helmikuu 2025

Päivittäiset lämpötilamittaukset:
- 2025-02-01: -3.2 °C
- 2025-02-02: -5.1 °C
...
```

Tulos — `kuvaus`-kenttä:
> "Helmikuu 2025 Helsingissä oli selvästi talvinen ja pakkasen hallitsema kuukausi. Kylmät jaksot seurasivat toisiaan tiuhaan, ja lumi pysyi maassa lähes koko kuukauden ajan. Säätila pysyi melko tasaisena ilman suuria lauhtumisjaksoja."

**Vaihe 3 — Lasketaan embedding**

`kuvaus`-tekstistä lasketaan 1536-ulotteinen float32-vektori `text-embedding-3-small`-mallilla. Embedding kuvaa tekstin **semanttisen merkityksen** numeroina.

**Vaihe 4 — Upsert Cosmos DB:hen**

Tallennetaan `kuukausiraportit`-containeriin:

```json
{
  "id": "Helsinki-2025-2",
  "paikkakunta": "Helsinki",
  "vuosi": 2025,
  "kuukausi": 2,
  "kuvaus": "Helmikuu 2025 Helsingissä oli selvästi talvinen...",
  "embedding": [0.012, -0.034, 0.071, ...]
}
```

> Historiakuukaudet generoidaan kerran käynnistyksessä. Kuluva kuukausi päivittyy 15 min välein — mutta vain jos uutta dataa on tullut sisään.

---

## Osa 2: RAG-haku kyselyvaiheessa (LlmGateway)

### Pyyntö

```bash
curl -X POST http://localhost:5079/api/chat \
  -H "X-Api-Key: YOUR-KEY" \
  -H "Content-Type: application/json" \
  -d '{"message": "Millainen talvi oli Helsingissä vuonna 2025?", "policy": "rag"}'
```

### Vaihe 1 — Kysymys embeddataan

`AzureOpenAIClient.GetEmbeddingAsync("Millainen talvi oli Helsingissä vuonna 2025?")`

Sama malli (`text-embedding-3-small`) kuin raporttien embeddauksessa → vektorit ovat vertailukelpoisia.

### Vaihe 2 — VectorDistance-haku (TOP 3)

`CosmosRagService.GetContextAsync(queryEmbedding)`:

```sql
SELECT TOP 3 c.paikkakunta, c.vuosi, c.kuukausi, c.kuvaus
FROM c
ORDER BY VectorDistance(c.embedding, @queryVector)
```

`VectorDistance` laskee kosinietäisyyden — pienempi arvo = semanttisesti lähempänä. Kysymys "talvi 2025 Helsinki" löytää todennäköisesti joulukuun 2024 ja tammi/helmikuun 2025 raportit Helsingistä.

Tulos (3 lähintä raporttia):
```
Helsinki 12/2024: Joulukuu oli poikkeuksellisen leuto — lunta ei juuri kertynyt...
Helsinki 1/2025: Tammikuu toi vihdoin talven — pakkasjaksot vahvistuivat...
Helsinki 2/2025: Helmikuu oli selvästi talvinen ja pakkasen hallitsema...
```

### Vaihe 3 — Konteksti injektoidaan system promptiin

`BuildRagSystemPrompt(ragContext, schema)` rakentaa system promptin:

```
Olet assistentti joka vastaa sääkysymyksiin...

Relevantit sääkuvaukset kontekstina:
Helsinki 12/2024: Joulukuu oli poikkeuksellisen leuto...
Helsinki 1/2025: Tammikuu toi vihdoin talven...
Helsinki 2/2025: Helmikuu oli selvästi talvinen...

Sinulla on käytettävissä työkalu:
- query_database: suorita Cosmos DB SQL -kysely tarkkoja lukuja varten

Tietokannan skeema:
c.content.paikkakunta, c.content.pvm, c.content.lampotila ...

TÄRKEÄ: Tarkat luvut haet AINA query_database-työkalulla.
Sääkuvaukset ovat vain kontekstia — älä käytä niistä lukuja suoraan vastauksessasi.
```

### Vaihe 4 — Agenttiloop

LLM tietää nyt kontekstin perusteella mistä kuukausista on kyse ja generoi kohdistetun SQL-kyselyn:

```json
{
  "tool_calls": [{
    "function": {
      "name": "query_database",
      "arguments": "{\"sql\": \"SELECT SUBSTRING(c.content.pvm,0,7) AS kk, AVG(c.content.lampotila) AS avg FROM c WHERE c.content.paikkakunta='Helsinki' AND (STARTSWITH(c.content.pvm,'2024-12') OR STARTSWITH(c.content.pvm,'2025-01') OR STARTSWITH(c.content.pvm,'2025-02')) GROUP BY SUBSTRING(c.content.pvm,0,7)\"}"
    }
  }]
}
```

SQL-tulos lisätään viesteihin, LLM vastaa lopullisesti:

> "Helsingin talvi 2024–2025 oli kaksijakoinen. Joulukuu 2024 oli poikkeuksellisen leuto (+2.1 °C keskiarvo), mutta tammikuusta 2025 talvi vahvistui merkittävästi (−4.3 °C) ja helmikuu 2025 oli pakkasvoittoinen (−5.1 °C). Kaiken kaikkiaan talvi alkoi myöhään mutta oli loppupuoliskollaan selvästi tavanomaista kylmempi."

---

## Miksi laadulliset kuvaukset ovat parempia kuin numerot containerissa

| Lähestymistapa | Ongelma |
|---|---|
| Numerot Cosmos-dokumenteissa | LLM voi käyttää stale-lukuja suoraan vastauksessaan ohittaen SQL-haun |
| Pelkkä SQL ilman RAGia | LLM ei tiedä etukäteen mistä ajanjaksosta on kyse — kysely kohdistuu huonosti tai LLM kysyy tarkennusta |
| **Kuvaukset + embedding + SQL** | RAG antaa kontekstin, SQL antaa tarkat luvut — yhdistelmä on sekä tarkka että sujuva |

---

## Tietorakenteet

### `documents`-container (mittausdataa)
```
c.id, c.content.paikkakunta, c.content.pvm (esim. "2025-02-15"), c.content.lampotila
```

### `kuukausiraportit`-container (RAG-raportit)
```
id:          "Helsinki-2025-2"
paikkakunta: "Helsinki"
vuosi:       2025
kuukausi:    2
kuvaus:      "Helmikuu 2025 Helsingissä oli..."   ← semanttinen haku kohdistuu tähän
embedding:   [1536 float32-arvoa]                 ← VectorDistance käyttää tätä
```

### Cosmos DB -konfiguraatio
- Tili: capability `EnableNoSQLVectorSearch`
- Container: `vectorEmbeddingPolicy` (float32, 1536 dim, cosine) + `indexingPolicy` (quantizedFlat)

---

## Policyt

| Policy | Mitä tekee |
|---|---|
| `chat_default` | Yksinkertainen chat, ei datahakua |
| `tools` | Agenttiloop + Cosmos DB SQL |
| `tools_sql` | Agenttiloop + MS SQL (T-SQL) |
| `rag` | **RAG-haku ensin, sitten agenttiloop + Cosmos DB SQL** |

---

## Testikutsu

```bash
# RAG-polku
curl -X POST http://localhost:5079/api/chat \
  -H "X-Api-Key: YOUR-API-KEY" \
  -H "Content-Type: application/json" \
  -d '{"message": "Millainen talvi oli Helsingissä vuonna 2025?", "policy": "rag"}'

# Vertailupiste — sama kysymys ilman RAGia
curl -X POST http://localhost:5079/api/chat \
  -H "X-Api-Key: YOUR-API-KEY" \
  -H "Content-Type: application/json" \
  -d '{"message": "Millainen talvi oli Helsingissä vuonna 2025?", "policy": "tools"}'
```

RAG-policy vastaa rikkaammin koska sillä on laadullinen konteksti ennen numerohakua. `tools`-policy saattaa vastata suppeammin tai kysyä tarkennusta ilman kontekstia.
