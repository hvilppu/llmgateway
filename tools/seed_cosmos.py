"""
Generoi ~500 testidokumenttia Cosmos DB:hen.
10 paikkakuntaa × 50 satunnaista päivää väliltä 2023-01-01 – 2025-12-31.

Asenna riippuvuus:
    pip install azure-cosmos

Ajo:
    python tools/seed_cosmos.py --connection-string "AccountEndpoint=..."
    tai aseta COSMOS_CONNECTION_STRING ympäristömuuttujaan
"""

import argparse
import os
import random
from datetime import date, timedelta

from azure.cosmos import CosmosClient

# ── Konfiguraatio ──────────────────────────────────────────────────────────────

DATABASE_NAME   = "ragdb"
CONTAINER_NAME  = "documents"
DOCS_PER_PLACE  = 50   # 10 paikkaa × 50 = 500 dokumenttia

LOCATIONS = [
    "Helsinki", "Tampere", "Turku", "Oulu", "Jyväskylä",
    "Rovaniemi", "Kuopio", "Lahti", "Joensuu", "Vaasa",
]

# Realistiset kuukausilämpötilat Suomessa (min, max) eteläisenä perustasona
BASE_TEMPS = {
    1:  (-15, -3),   # tammikuu
    2:  (-14, -2),
    3:  (-8,   3),
    4:  (-2,  10),
    5:  (5,   17),
    6:  (11,  22),
    7:  (14,  25),
    8:  (13,  23),
    9:  (7,   17),
    10: (1,   10),
    11: (-5,   4),
    12: (-12,  1),   # joulukuu
}

# Pohjoiset paikkakunnat ovat kylmempiä
NORTH_OFFSET = {
    "Rovaniemi": -5,
    "Oulu":      -3,
    "Joensuu":   -2,
    "Kuopio":    -1,
}

# ── Pääohjelma ────────────────────────────────────────────────────────────────

def generate_docs() -> list[dict]:
    start = date(2023, 1, 1)
    end   = date(2025, 12, 31)
    total_days = (end - start).days + 1

    docs = []
    idx  = 1

    for location in LOCATIONS:
        offset  = NORTH_OFFSET.get(location, 0)
        day_offsets = sorted(random.sample(range(total_days), DOCS_PER_PLACE))

        for day_offset in day_offsets:
            d = start + timedelta(days=day_offset)
            lo, hi = BASE_TEMPS[d.month]
            temp = round(random.uniform(lo + offset, hi + offset), 1)

            docs.append({
                "id": f"doc-{idx:04d}",
                "content": {
                    "paikkakunta": location,
                    "pvm":         d.isoformat(),   # "2024-07-15"
                    "lämpötila":   temp,
                },
            })
            idx += 1

    return docs


def main():
    parser = argparse.ArgumentParser(description="Seed Cosmos DB with test temperature data")
    parser.add_argument("--connection-string", default=os.getenv("COSMOS_CONNECTION_STRING"),
                        help="Cosmos DB connection string (tai COSMOS_CONNECTION_STRING env)")
    parser.add_argument("--database",  default=DATABASE_NAME)
    parser.add_argument("--container", default=CONTAINER_NAME)
    args = parser.parse_args()

    if not args.connection_string:
        raise SystemExit("Virhe: anna --connection-string tai aseta COSMOS_CONNECTION_STRING")

    client    = CosmosClient.from_connection_string(args.connection_string)
    container = client.get_database_client(args.database).get_container_client(args.container)

    docs = generate_docs()
    print(f"Lisätään {len(docs)} dokumenttia kantaan {args.database}/{args.container}...")

    for doc in docs:
        container.upsert_item(doc)
        c = doc["content"]
        print(f"  {doc['id']}  {c['paikkakunta']:<12}  {c['pvm']}  {c['lämpötila']:>6}°C")

    print(f"\nValmis! {len(docs)} dokumenttia lisätty.")


if __name__ == "__main__":
    main()
