# Termit ja lyhenteet — LlmGateway

Tärkeimmät käsitteet, lyhenteet ja suunnittelumallit tässä projektissa.

---

## AI / LLM -termit

| Lyhenne / Termi | Selitys |
|-----------------|---------|
| **LLM** | Large Language Model — suuri kielimalli (esim. GPT-4). Generoi tekstiä syötteen perusteella. |
| **GPT** | Generative Pre-trained Transformer — OpenAI:n LLM-arkkitehtuuri. |
| **GPT-4 / GPT-4o Mini** | OpenAI:n malliversioita. Mini = halvempi ja nopeampi, GPT-4 = tarkempi. |
| **Token** | Mallin käsittelemä tekstin perusyksikkö (~¾ sanaa). Laskutus perustuu tokeneihin. |
| **Prompt** | Käyttäjän syöte / kysymys mallille. |
| **Completion** | Mallin tuottama vastaus promptiin. |
| **RAG** | Retrieval-Augmented Generation — malli täydennetään hakemalla relevanttia tietoa ulkoisesta lähteestä ennen vastausta. |
| **Embedding** | Tekstin numeerinen vektorimuoto semanttista hakua varten. Käytetään RAG-putkissa. |
| **Deployment** | Azure OpenAI -resurssin sisäinen nimi yhdelle käyttöönotetulle malliversiolle. |
| **System message** | Mallin käyttäytymistä ohjaava viesti, joka lähetetään ennen käyttäjän promptia. |

---

## Azure / Infrastruktuuri

| Termi | Selitys |
|-------|---------|
| **Azure OpenAI** | Microsoftin hallinnoitu OpenAI-palvelu Azure-pilvessä. REST API yhteensopiva OpenAI:n kanssa. |
| **Endpoint** | Azure OpenAI -resurssin URL-osoite, johon API-kutsut tehdään. |
| **API Key** | Autentikointiavain Azure OpenAI -kutsuihin. |
| **API Version** | Azure OpenAI REST API:n versioparametri (esim. `2024-02-15-preview`). |
| **HTTP 429** | Too Many Requests — rate limit ylitetty, pitää odottaa. |
| **HTTP 5xx** | Palvelinvirhe — transientti virhe, hyvä kohde uudelleenyritykselle. |
| **HTTP 502** | Bad Gateway — ylävirran palvelu (Azure) vastasi virheellisesti. |
| **HTTP 503** | Service Unavailable — tässä projektissa: circuit breaker on auki. |

---

## Resilienssi­mallit

| Termi | Selitys |
|-------|---------|
| **Circuit Breaker** (katkaisin) | Suunnittelumalli: jos virheitä tulee liian monta peräkkäin (`FailureThreshold`), "katkaisin aukeaa" ja kutsut estetään hetkeksi (`BreakDurationSeconds`). Estää ylikuormittamasta jo rikkinäistä palvelua. Tilat: **Closed** (normaali) → **Open** (estetty) → **Half-Open** (kokeillaan taas). |
| **Retry** (uudelleenyritys) | Epäonnistunut kutsu yritetään uudelleen `MaxRetries` kertaa, viive kasvaa eksponentiaalisesti. |
| **Exponential Backoff** | Viive uudelleenyritysten välillä kasvaa potenssisarjana (esim. 500 ms → 1000 ms → 2000 ms). |
| **Timeout** | Kutsu perutaan jos se kestää liian kauan (`TimeoutMs`). Toteutettu `CancellationTokenSource.CancelAfter`. |
| **Transient Error** | Ohimenevä virhe (verkko-ongelma, rate limit) — sopii uudelleenyritykselle. |
| **Failure Threshold** | Peräkkäisten virheiden määrä, jonka jälkeen circuit breaker aukeaa. |
| **Break Duration** | Aika, jonka circuit breaker pysyy auki ennen kuin kokeillaan uudelleen. |

---

## .NET / ASP.NET Core

| Termi | Selitys |
|-------|---------|
| **Minimal API** | ASP.NET Core -tapa määritellä endpointit ilman controllereita (`app.MapPost(...)`). |
| **DI / Dependency Injection** | Riippuvuuksien injektointi — ASP.NET Core:n sisäänrakennettu IoC-kontti. |
| **IOptions\<T\>** | .NET:n tapa lukea konfiguraatio vahvasti tyypitetysti. |
| **IHttpClientFactory** | .NET:n suositeltu tapa luoda `HttpClient`-instansseja (hallitsee elinkaaret). |
| **Typed Client** | `IHttpClientFactory`-malli, jossa `HttpClient` injektoidaan suoraan omaan palveluluokkaan. |
| **Middleware** | ASP.NET Core -putki, jossa HTTP-pyyntö kulkee ennen endpointia (esim. autentikointi, lokitus). |
| **OpenAPI** | REST API:n kuvausstandardi (aiemmin Swagger). .NET 10:ssä sisäänrakennettu (`AddOpenApi`). |
| **Structured Logging** | Lokitus avain-arvo-pareina (`Policy=chat_default ModelKey=gpt4oMini`) — helpompi hakea ja suodattaa. |

---

## Projektispesifiset termit

| Termi | Selitys |
|-------|---------|
| **Policy** | Nimetty reitityssääntö (esim. `chat_default`, `critical`). Määrittää, mitä mallia käytetään. |
| **ModelKey** | Projektin sisäinen tunniste mallille (esim. `gpt4`, `gpt4oMini`). Mapautuu Azure-deployment-nimeen. |
| **RoutingEngine** | Komponentti, joka muuntaa policyn modelKey:ksi konfiguraation perusteella. |
| **Deployment Name** | Azure OpenAI:n sisäinen nimi käyttöönotetulle mallille. Haetaan konfigista `Deployments[modelKey]`. |
| **RequestId** | Yksilöivä tunniste jokaiselle pyynnölle (`httpContext.TraceIdentifier`). Näkyy vastauksessa ja lokeissa. |
| **ChatRequest** | Sisääntuleva pyyntö-DTO: `{ message, policy }`. |
| **ChatResponse** | Uloslähtevä vastaus-DTO: `{ reply, model, usage, requestId }`. |
| **UsageInfo** | Token-kulutustieto vastauksessa: `promptTokens`, `completionTokens`, `totalTokens`. |
