# Termit ja lyhenteet — LlmGateway

Tärkeimmät käsitteet, lyhenteet ja suunnittelumallit tässä projektissa.

---

## AI / LLM

| Termi | Selitys |
|-------|---------|
| **LLM** | Large Language Model — suuri kielimalli (esim. GPT-4). Generoi tekstiä syötteen perusteella. |
| **GPT** | Generative Pre-trained Transformer — OpenAI:n LLM-arkkitehtuuri. |
| **GPT-4 / GPT-4o Mini** | OpenAI:n malliversioita. Mini = halvempi ja nopeampi, GPT-4 = tarkempi. |
| **Token** | Mallin käsittelemä tekstin perusyksikkö (~¾ sanaa). Laskutus perustuu tokeneihin. |
| **Prompt** | Käyttäjän syöte / kysymys mallille. |
| **Completion** | Mallin tuottama vastaus promptiin. |
| **System message** | Mallin käyttäytymistä ohjaava viesti, joka lähetetään ennen käyttäjän promptia. Rakennetaan dynaamisesti skeemalla ja/tai RAG-kontekstilla. |

---

## Function calling ja agenttiloop

| Termi | Selitys |
|-------|---------|
| **Function Calling** | OpenAI:n mekanismi, jossa LLM voi pyytää ulkoisen funktion suoritusta vastauksensa sijaan. Mahdollistaa tietokannan kyselemisen luonnollisella kielellä. |
| **Tool Call** | Yksittäinen funktion kutsu LLM:n vastauksessa (`finish_reason: "tool_calls"`). Sisältää funktion nimen ja argumentit JSON-muodossa. |
| **Agenttiloop** | `RunAgentLoopAsync` — iteratiivinen silmukka, jossa LLM kutsuu työkaluja kunnes `finish_reason: "stop"`. Yläraja: `MaxToolIterations = 5`. |
| **Finish Reason** | Azure OpenAI:n palauttama arvo, joka kertoo miksi generointi päättyi: `"stop"` = vastaus valmis, `"tool_calls"` = LLM haluaa kutsua työkalua. |
| **MaxToolIterations** | Agenttilooppin turvaraja (= 5). Estää äärettömän silmukan, jos LLM ei päädy `stop`-tilaan. |
| **query_database** | Projektin ainoa tool-funktio. LLM generoi SQL-kyselyn (Cosmos DB NoSQL tai T-SQL), gateway ajaa sen ja palauttaa tuloksen takaisin LLM:lle. |

---

## RAG ja vektorihaku

| Termi | Selitys |
|-------|---------|
| **RAG** | Retrieval-Augmented Generation — malli täydennetään hakemalla relevanttia tietoa ulkoisesta lähteestä ennen vastausta. |
| **Embedding** | Tekstin numeerinen vektorimuoto semanttista hakua varten. Tässä projektissa käytetään `text-embedding-3-small` -mallia (1536 dimensiota). |
| **Vektorihaku** | Etsii semanttisesti samankaltaisimmat dokumentit vertaamalla embedding-vektoreita. Käytetään RAG-kontekstin hakemiseen. |
| **VectorDistance** | Cosmos DB:n SQL-funktio vektorien välisen etäisyyden laskemiseen. Pienempi arvo = lähempänä = relevanttimpi. |
| **TopK** | Vektorihaun palautettavien dokumenttien maksimimäärä (oletuksena 3). Konfiguroidaan `Rag:TopK`-asetuksella. |
| **Kuukausiraportti** | RAG-indeksin dokumentti: LLM:n generoima sanallinen kuvaus yhden kaupungin yhdestä kuukaudesta, tallennettuna embedding-vektorin kanssa `kuukausiraportit`-containeriin. |
| **RAG-konteksti** | `IRagService.GetContextAsync` — vektorihaun palauttamat kuukausikuvaukset merkkijonona. Injektoidaan system promptiin ennen agenttilooppia. |
| **ReportBackfillService** | `IHostedService` SyncFunction-sovelluksessa. Ajaa `GenerateAllMonthsReportsAsync` kerran käynnistyksen yhteydessä — täyttää puuttuvat kuukausiraportit historiadatasta. Kuluvan kuukauden päivityksestä vastaa `CosmosToSqlTrigger`. |

---

## Tietokannat

| Termi | Selitys |
|-------|---------|
| **Cosmos DB** | Microsoftin hallinnoitu NoSQL-tietokanta Azure-pilvessä. Tässä projektissa käytetään SQL-rajapintaa mittausdatan ja kuukausiraporttien tallentamiseen sekä vektorihaun suorittamiseen. |
| **MS SQL / Azure SQL** | Microsoftin relaatiotietokanta. Vaihtoehtoinen backend mittausdatalle (`tools_sql`-policy). |
| **NoSQL (Cosmos DB SQL)** | Cosmos DB:n oma SQL-murre. Syntaksi muistuttaa SQL:ää mutta tukee vain osaa ominaisuuksista: `GROUP BY` + `ORDER BY` ei toimi yhdessä, ei `MONTH()`/`YEAR()`-funktioita. |
| **T-SQL** | Transact-SQL — Microsoft SQL Serverin SQL-murre. Käytä `TOP N` eikä `LIMIT`, tukee `MONTH()`/`YEAR()` ja `ORDER BY` + `GROUP BY` yhdistettynä. |
| **IQueryService** | Yhteinen rajapinta molemmille query-backendeille. Toteutukset: `CosmosQueryService` (keyed: `"cosmos"`) ja `SqlQueryService` (keyed: `"mssql"`). Vain SELECT-kyselyt sallittu. `CosmosQueryService` tarkistaa `StartsWith("SELECT")`; `SqlQueryService` käyttää AST-pohjaista validointia (`TSql160Parser`, `BlockedSchemaVisitor`) — estää myös sys-skeeman, OPENROWSET:n ja OPENQUERY:n. |
| **MaxRows** | `CosmosOptions.MaxRows` ja `SqlOptions.MaxRows` (oletuksena 500). Rajoittaa yhdestä `query_database`-kutsusta palautettavien rivien enimmäismäärän — estää liian suurten tulosjoukkojen palautuksen LLM:lle. |
| **ISchemaProvider** | Rajapinta skeeman lukemiseen. Toteutukset: `CosmosSchemaProvider` (keyed: `"cosmos"`) ja `SqlSchemaProvider` (keyed: `"mssql"`). Palauttavat kovakoodatun `const string` -skeeman suoraan koodista (`SchemaService.cs`). Tietokantaa ei kyselläSQL-skeeman hakemiseksi. |
| **Staattinen skeema** | Tietokannan kenttärakenne on kovakoodattu `SchemaService.cs`:ään (`const string`). Skeema injektoidaan system promptiin ennen LLM-kutsua jotta LLM osaa generoida oikean SQL-syntaksin. Päivitetään manuaalisesti koodiin kun tietokannan rakenne muuttuu. |

---

## Azure / Infrastruktuuri

| Termi | Selitys |
|-------|---------|
| **Azure OpenAI** | Microsoftin hallinnoitu OpenAI-palvelu Azure-pilvessä. REST API yhteensopiva OpenAI:n kanssa. |
| **Deployment** | Azure OpenAI -resurssin sisäinen nimi yhdelle käyttöönotetulle malliversiolle. Eri kuin mallin yleinen nimi (esim. deployment `"my-gpt4"` → malli `"gpt-4"`). |
| **Endpoint** | Azure OpenAI -resurssin URL-osoite, johon API-kutsut tehdään (`https://RESURSSI.openai.azure.com/`). |
| **API Key** | Autentikointiavain Azure OpenAI -kutsuihin. |
| **API Version** | Azure OpenAI REST API:n versioparametri URL:ssa (esim. `2024-02-15-preview`). |
| **APIM** | Azure API Management — Microsoftin hallinnoitu API-gateway. Hoitaa autentikoinnin, rate limitingin, reitityksen ja monitoroinnin infrastruktuuritasolla. Toimii tämän LlmGateway-sovelluksen *edessä*, ei korvaa sovellustason `RoutingEngine`ä. |
| **HTTP 429** | Too Many Requests — Azure OpenAI:n rate limit ylitetty. Transientti virhe, trigger circuit breakerille ja retrylle. |
| **HTTP 5xx** | Palvelinvirhe — transientti virhe, trigger circuit breakerille ja retrylle. |
| **HTTP 502** | Bad Gateway — ylävirran palvelu (Azure) vastasi virheellisesti. Tämän projektin vastaus Azure-virhetilanteessa. |
| **HTTP 503** | Service Unavailable — tässä projektissa: circuit breaker on auki kaikille malleille. |

---

## Resilienssiimallit

| Termi | Selitys |
|-------|---------|
| **Circuit Breaker** (katkaisin) | Suunnittelumalli: jos virheitä tulee liian monta peräkkäin (`FailureThreshold`), "katkaisin aukeaa" ja kutsut estetään hetkeksi (`BreakDurationSeconds`). Tilat: **Closed** (normaali) → **Open** (estetty) → **Half-Open** (kokeillaan taas). |
| **Failure Threshold** | Peräkkäisten virheiden määrä, jonka jälkeen circuit breaker aukeaa. |
| **Break Duration** | Aika, jonka circuit breaker pysyy auki ennen kuin kokeillaan uudelleen (`BreakDurationSeconds`). |
| **Retry** (uudelleenyritys) | Epäonnistunut kutsu yritetään uudelleen `MaxRetries` kertaa. Koskee transientteja virheitä (408, 429, 5xx, timeout). |
| **Lineaarinen viive** | Viive uudelleenyritysten välillä: `RetryDelayMs × yritys` (esim. 500 ms → 1000 ms → 1500 ms). Ei eksponentiaalinen. |
| **Timeout** | Kutsu perutaan jos se kestää liian kauan (`TimeoutMs`). Toteutettu `CancellationTokenSource.CancelAfter`. Timeout rekisteröidään circuit breakerille virheenä. |
| **Transient Error** | Ohimenevä virhe (verkko-ongelma, rate limit, timeout) — sopii uudelleenyritykselle ja merkitään circuit breakerille. |

---

## Projektispesifinen routing

| Termi | Selitys |
|-------|---------|
| **Policy** | Nimetty reitityssääntö (esim. `chat_default`, `tools`, `tools_sql`, `rag`). Määrittää mallin, toimintatavan ja query-backendin. Lähetetään `ChatRequest.Policy`-kentässä. |
| **PolicyConfig** | Yhden policyn konfiguraatio: `PrimaryModel`, `Fallbacks`, `ToolsEnabled`, `QueryBackend`, `RagEnabled`. |
| **ModelKey** | Projektin sisäinen tunniste mallille (esim. `"gpt4"`, `"gpt4oMini"`). Mapautuu Azure-deployment-nimeen `AzureOpenAIOptions.Deployments`-dictionaryssä. |
| **Model Chain** | `RoutingEngine.ResolveModelChain` palauttaa järjestetyn listan `[primary, fallback1, ...]`. Kutsutaan järjestyksessä kunnes yksi onnistuu. |
| **Fallback** | Varamallit, joita käytetään jos primary-malli epäonnistuu (circuit breaker tai HTTP-virhe). Konfiguroidaan policyn `Fallbacks`-listassa. |
| **QueryBackend** | Policyn asetus (`"cosmos"` tai `"mssql"`), joka määrittää mitä `IQueryService`- ja `ISchemaProvider`-toteutusta käytetään. |
| **RoutingEngine** | `IRoutingEngine` — muuntaa pyynnön policyn modelKeyksi ja kertoo onko työkalut / RAG käytössä. |

---

## .NET / ASP.NET Core

| Termi | Selitys |
|-------|---------|
| **Minimal API** | ASP.NET Core -tapa määritellä endpointit ilman controllereita (`app.MapPost(...)`). |
| **DI / Dependency Injection** | Riippuvuuksien injektointi — ASP.NET Core:n sisäänrakennettu IoC-kontti. |
| **Keyed DI** | `AddKeyedSingleton` + `[FromKeyedServices("avain")]` — mahdollistaa saman rajapinnan useamman toteutuksen rekisteröimisen eri avaimilla. Käytetään `IQueryService` ja `ISchemaProvider` -backendeille (`"cosmos"` / `"mssql"`). |
| **IOptions\<T\>** | .NET:n tapa lukea konfiguraatio vahvasti tyypitetysti. |
| **IHttpClientFactory** | .NET:n suositeltu tapa luoda `HttpClient`-instansseja (hallitsee elinkaaret, välttää socket exhaustion). |
| **Typed Client** | `IHttpClientFactory`-malli, jossa `HttpClient` injektoidaan suoraan omaan palveluluokkaan (`AzureOpenAIClient`). |
| **Middleware** | ASP.NET Core -putki, jossa HTTP-pyyntö kulkee ennen endpointia. Tässä projektissa: `ApiKeyMiddleware` (`X-Api-Key`-headerintarkistus). |
| **SSE** | Server-Sent Events — HTTP-pohjainen yksisuuntainen push-protokolla (`text/event-stream`). `/api/chat/stream` käyttää SSE:tä streaming-vastauksiin. Event-tyypit: `status` (työkalukutsujen tila), `token` (vastausteksti pala palalta), `done` (valmis + metatiedot), `error` (virhe), `sql` (LLM:n generoima SQL-kysely — näytetään UI:ssa toast-ilmoituksena). |
| **OpenAPI** | REST API:n kuvausstandardi. .NET 10:ssä sisäänrakennettu (`AddOpenApi` / `MapOpenApi`), ei Swashbucklea. |
| **Structured Logging** | Lokitus avain-arvo-pareina (`Policy=`, `ModelKey=`, `LatencyMs=`, `Backend=`) — helpompi hakea ja suodattaa log-järjestelmistä. |

---

## DTO:t ja mallit

| Termi | Selitys |
|-------|---------|
| **ChatRequest** | Sisääntuleva pyyntö-DTO: `{ message, policy, conversationId }`. |
| **ChatResponse** | Uloslähtevä vastaus-DTO: `{ reply, model, usage, requestId }`. |
| **UsageInfo** | Token-kulutustieto vastauksessa: `promptTokens`, `completionTokens`, `totalTokens`. |
| **ConversationId** | `ChatRequest`-kentän varaus tulevaa keskusteluhistoriaa varten — ei käytössä vielä. |
| **RequestId** | Yksilöivä tunniste jokaiselle pyynnölle (`httpContext.TraceIdentifier`). Näkyy vastauksessa ja lokeissa. |
| **AzureRawCompletion** | `GetRawCompletionAsync`:n palauttama raaka vastaus agenttilooppia varten. Sisältää `FinishReason`, `Content`, `ToolCalls` ja `Usage`. |
