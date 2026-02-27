seed_cosmos.py — Cosmos DB testidatan generointi
=================================================

Generoi 500 testidokumenttia Cosmos DB:hen:
  10 paikkakuntaa x 50 satunnaista päivää väliltä 2023-01-01 – 2025-12-31

Dokumentin rakenne:
  { id, content: { paikkakunta, pvm, lämpötila } }

Paikkakunnat:
  Helsinki, Tampere, Turku, Oulu, Jyväskylä,
  Rovaniemi, Kuopio, Lahti, Joensuu, Vaasa


ASENNUS
-------

  pip install azure-cosmos


AJO
---

Vaihtoehto 1 — parametrina:

  python tools/seed_cosmos.py --connection-string "AccountEndpoint=https://my-cosmos-account.documents.azure.com:443/;AccountKey=...;"

Vaihtoehto 2 — ympäristömuuttujana:

  export COSMOS_CONNECTION_STRING="AccountEndpoint=https://my-cosmos-account.documents.azure.com:443/;AccountKey=...;"
  python tools/seed_cosmos.py

Muut parametrit (valinnaisia):

  --database    Tietokannan nimi  (oletus: ragdb)
  --container   Containerin nimi  (oletus: documents)


MISSÄ CONNECTION STRING ON?
---------------------------

  Azure Portal → Cosmos DB -tili → Keys → PRIMARY CONNECTION STRING


TESTAUS SEEDING JÄLKEEN
-----------------------

  curl -X POST http://localhost:5079/api/chat \
    -H "Content-Type: application/json" \
    -H "X-Api-Key: YOUR-API-KEY" \
    -d "{\"message\": \"Mikä oli lämpötilan keskiarvo Helsingissä helmikuussa 2025?\", \"policy\": \"tools\"}"
