# LlmGateway

ASP.NET Core 10 minimal API gateway for Azure OpenAI, with retry, timeout, circuit breaker, RAG and function calling.

Infrastruktuuri ja käyttöönotto: [INFRA.md](INFRA.md)

---

## Policyit ja datan käyttö

Gateway rajoittaa LLM:n vastaamaan **ainoastaan Suomen kaupunkien lämpötila- ja säädataan** liittyviin kysymyksiin.

| Policy | Malli | Datan käyttö | Milloin käyttää |
|--------|-------|--------------|-----------------|
| *(ei policyä)* / `chat_default` | gpt-4o-mini | Ei — LLM:n oma tieto | Ei suositella datakysymyksiin |
| `critical` | gpt-4o | Ei — LLM:n oma tieto | Vaativampi päättely ilman dataa |
| `tools` | gpt-4o | **Kyllä** — kyselee Cosmos DB:stä | Kaikki datakysymykset |

`tools`-policyssä LLM päättää itse kutsuuko `query_database`-työkalua:
- *"Mikä oli lämpötilan keskiarvo Helsingissä helmikuussa 2025?"* → kyselee kannasta
- *"Miksi lämpötila vaihtelee vuodenajan mukaan?"* → vastaa suoraan

---

## Testaus

Kaikki pyynnöt vaativat `X-Api-Key` -headerin.

Datakysymys — LLM hakee Cosmos DB:stä (`tools`-policy):
```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Mikä oli lämpötilan keskiarvo Helsingissä helmikuussa 2025?\", \"policy\": \"tools\"}"
```

Selittävä kysymys — LLM vastaa suoraan (`tools`-policy, ei työkalukutsua):
```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Selitä miksi lämpötila vaihtelee vuodenajan mukaan\", \"policy\": \"tools\"}"
```

Aiheen ulkopuolinen kysymys — LLM kieltäytyy:
```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Hei\"}"
```



# Myöhemmin

RAG:in myöhemmin käyttöön, tarvitaan:
  1. Container uudelleen vektori-indeksillä (poistettu     
  komento palautetaan)
  2. Dokumenttien indeksointi — ajaa embedding-API:n       
  jokaiselle dokumentille ja tallentaa embedding-kenttään 

Toinen lähde kuten TableContainer
