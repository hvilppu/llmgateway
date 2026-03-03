Tarkista että seuraavat dokumentaatiotiedostot ovat ajan tasalla suhteessa koodiin. Käy jokainen kohta läpi järjestyksessä ja raportoi löydökset.

## 1. CLAUDE.md — Rakenne

Tarkista että jokainen tiedosto `Rakenne`-osiossa vastaa oikeaa tiedostoa levyllä:
- Lue `Endpoints/ChatEndpoints.cs`, `Services/QueryService.cs`, `Services/SchemaService.cs`, `Services/RagService.cs`, `Routing/Routing.cs`, `Infrastructure/AzureOpenAIClient.cs`
- Vertaa CLAUDE.md:n Rakenne-osion kuvauksia koodin todelliseen sisältöön
- Tarkista erityisesti: metodinimet, rajapinnat, luokkien nimet

## 2. CLAUDE.md — Konfiguraatio

Tarkista että appsettings-esimerkki vastaa todellista `appsettings.json`-tiedostoa:
- Lue `appsettings.json`
- Vertaa kaikki kentät CLAUDE.md:n Konfiguraatio-osioon
- Erityisesti: Policyt, Rag-osio, AzureOpenAI-osio

## 3. CLAUDE.md — Policyt

Tarkista `Routing/Routing.cs`:sta löytyvät PolicyConfigit ja vertaa CLAUDE.md:n Policy-pohjainen routing -osioon. Kaikki käytössä olevat policyit pitää olla kuvattuna.

## 4. TERMS.md

Tarkista onko koodissa käsitteitä tai komponentteja joita ei ole TERMS.md:ssä. Lue erityisesti uudet tai muuttuneet tiedostot.

## 5. architecture.mmd

Tarkista onko kaaviossa kaikki pääkomponentit:
- Kaikki services (QueryService, SchemaService, RagService)
- Kaikki backendit (Cosmos DB, MS SQL, Azure Embeddings)
- SyncFunction
- Routing-polut (yksinkertainen, tools, RAG)

## Raportointi

Listaa löydökset muodossa:
- ✅ OK — jos kunnossa
- ⚠️ Puuttuu / vanhentunut — jos jotain pitää päivittää, kerro tarkasti mitä

Älä tee muutoksia — vain raportoi. Kysy käyttäjältä ennen kuin muokkaat mitään.
