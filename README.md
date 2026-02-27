# LlmGateway

ASP.NET Core 10 minimal API gateway for Azure OpenAI, with retry, timeout, circuit breaker, RAG and function calling.

Infrastruktuuri ja käyttöönotto: [INFRA.md](INFRA.md)

---

## Testaus

Kaikki pyynnöt vaativat `X-Api-Key` -headerin.

```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Hei\"}"
```

```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Analysoi tämä\", \"policy\": \"critical\"}"
```

```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Mikä oli lämpötilan keskiarvo Helsingissä helmikuussa 2025?\", \"policy\": \"tools\"}"
```

```bash
curl -X POST https://llmgateway-prod.azurewebsites.net/api/chat -H "Content-Type: application/json" -H "X-Api-Key: YOUR-API-KEY" -d "{\"message\": \"Selitä miksi lämpötila vaihtelee vuodenajan mukaan\", \"policy\": \"tools\"}"
```



# Myöhemmin

RAG:in myöhemmin käyttöön, tarvitaan:
  1. Container uudelleen vektori-indeksillä (poistettu     
  komento palautetaan)
  2. Dokumenttien indeksointi — ajaa embedding-API:n       
  jokaiselle dokumentille ja tallentaa embedding-kenttään 

Toinen lähde kuten TableContainer
