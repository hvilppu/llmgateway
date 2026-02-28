"""
Migraatio: Cosmos DB → MS SQL Server

Lukee kaikki dokumentit Cosmos DB -containerista ja kirjoittaa ne
mittaukset-tauluun MS SQL -kantaan. Dokumenteissa odotetaan kenttiä:
  id, paikkakunta, pvm, lampotila

Asenna riippuvuudet:
    pip install azure-cosmos pyodbc

Ajo:
    python tools/seed_mssql.py \\
        --cosmos-connection-string "AccountEndpoint=...;AccountKey=..." \\
        --cosmos-database mydb \\
        --cosmos-container documents \\
        --mssql-connection-string "Server=...;Database=llmgateway;User Id=sqladmin;Password=...;Encrypt=yes;TrustServerCertificate=no;"

    tai aseta ympäristömuuttujat:
        COSMOS_CONNECTION_STRING, COSMOS_DATABASE, COSMOS_CONTAINER,
        MSSQL_CONNECTION_STRING
"""

import argparse
import os
import sys

from azure.cosmos import CosmosClient
import pyodbc

# ── DDL ────────────────────────────────────────────────────────────────────────

CREATE_TABLE_SQL = """
IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = 'mittaukset'
)
BEGIN
    CREATE TABLE mittaukset (
        id          NVARCHAR(50)  NOT NULL PRIMARY KEY,
        paikkakunta NVARCHAR(100) NOT NULL,
        pvm         DATE          NOT NULL,
        lampotila   FLOAT         NOT NULL
    );
    CREATE INDEX IX_mitt_paikkakunta_pvm ON mittaukset (paikkakunta, pvm);
END
"""

UPSERT_SQL = (
    "MERGE mittaukset AS t "
    "USING (VALUES (?, ?, ?, ?)) AS s(id, paikkakunta, pvm, lampotila) "
    "ON t.id = s.id "
    "WHEN MATCHED THEN UPDATE SET "
    "    paikkakunta = s.paikkakunta, pvm = s.pvm, lampotila = s.lampotila "
    "WHEN NOT MATCHED THEN INSERT (id, paikkakunta, pvm, lampotila) "
    "VALUES (s.id, s.paikkakunta, s.pvm, s.lampotila);"
)

# ── Cosmos-luku ────────────────────────────────────────────────────────────────

def read_cosmos_documents(conn_str: str, database: str, container: str) -> list[dict]:
    """Lukee kaikki dokumentit Cosmos DB -containerista."""
    client = CosmosClient.from_connection_string(conn_str)
    cont = client.get_database_client(database).get_container_client(container)

    print(f"Luetaan dokumentteja Cosmos DB:stä ({database}/{container})...")

    docs = []
    for item in cont.query_items("SELECT * FROM c", enable_cross_partition_query=True):
        docs.append(item)

    print(f"  Löydetty {len(docs)} dokumenttia.")
    return docs


def extract_row(doc: dict) -> tuple | None:
    """Poimii mittausriviin tarvittavat kentät dokumentista. Palauttaa None jos kentät puuttuvat.

    Tukee kahta rakennetta:
      - kentät ylätasolla: { id, paikkakunta, pvm, lampotila, ... }
      - kentät content-objektissa: { id, content: { paikkakunta, pvm, lampotila }, ... }
    """
    doc_id = doc.get("id")

    # Kokeile ensin ylätaso, sitten content-aliobjekti
    source = doc
    content = doc.get("content")
    if isinstance(content, dict):
        # content on objekti — käytä sitä kenttien lähteenä jos ylätasolta puuttuu
        if doc.get("paikkakunta") is None:
            source = content

    paikkakunta = source.get("paikkakunta")
    pvm         = source.get("pvm")
    lampotila   = source.get("lampotila") or source.get("lämpötila")

    if any(v is None for v in (doc_id, paikkakunta, pvm, lampotila)):
        return None

    return (str(doc_id), str(paikkakunta), str(pvm), float(lampotila))

# ── MS SQL -kirjoitus ──────────────────────────────────────────────────────────

def write_to_mssql(conn_str: str, rows: list[tuple]) -> None:
    """Luo taulun (jos ei ole) ja upsertaa rivit MS SQL -kantaan."""
    # Lisää Driver automaattisesti jos puuttuu
    if "Driver=" not in conn_str and "driver=" not in conn_str:
        conn_str = "Driver={ODBC Driver 17 for SQL Server};" + conn_str
    # Muunna .NET SqlClient -avainsanat ODBC-muotoon
    conn_str = conn_str.replace("User Id=", "UID=").replace("user id=", "UID=")
    conn_str = conn_str.replace("Password=", "PWD=").replace("password=", "PWD=")
    conn = pyodbc.connect(conn_str)
    conn.autocommit = False
    cursor = conn.cursor()

    print("Luodaan taulu mittaukset (jos ei ole)...")
    cursor.execute(CREATE_TABLE_SQL)
    conn.commit()

    print(f"Kirjoitetaan {len(rows)} riviä tauluun mittaukset...")
    ok = 0
    for row in rows:
        cursor.execute(UPSERT_SQL, row)
        print(f"  {row[0]}  {row[1]:<12}  {row[2]}  {row[3]:>6}°C")
        ok += 1

    conn.commit()
    conn.close()
    print(f"\nValmis! {ok}/{len(rows)} riviä kirjoitettu.")

# ── Pääohjelma ─────────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(
        description="Migroi Cosmos DB -dokumentit MS SQL -tietokantaan"
    )
    parser.add_argument(
        "--cosmos-connection-string",
        default=os.getenv("COSMOS_CONNECTION_STRING"),
        help="Cosmos DB -yhteysmerkkijono (tai COSMOS_CONNECTION_STRING env)",
    )
    parser.add_argument(
        "--cosmos-database",
        default=os.getenv("COSMOS_DATABASE", "mydb"),
        help="Cosmos DB -tietokannan nimi (tai COSMOS_DATABASE env)",
    )
    parser.add_argument(
        "--cosmos-container",
        default=os.getenv("COSMOS_CONTAINER", "documents"),
        help="Cosmos DB -containerin nimi (tai COSMOS_CONTAINER env)",
    )
    parser.add_argument(
        "--mssql-connection-string",
        default=os.getenv("MSSQL_CONNECTION_STRING"),
        help="MS SQL -yhteysmerkkijono (tai MSSQL_CONNECTION_STRING env)",
    )
    args = parser.parse_args()

    if not args.cosmos_connection_string:
        raise SystemExit("Virhe: anna --cosmos-connection-string tai aseta COSMOS_CONNECTION_STRING")
    if not args.mssql_connection_string:
        raise SystemExit("Virhe: anna --mssql-connection-string tai aseta MSSQL_CONNECTION_STRING")

    docs = read_cosmos_documents(
        args.cosmos_connection_string,
        args.cosmos_database,
        args.cosmos_container,
    )

    rows = []
    ohitettu = 0
    for doc in docs:
        row = extract_row(doc)
        if row is None:
            print(f"  OHITETTU (puuttuvat kentät): {doc.get('id', '?')}", file=sys.stderr)
            ohitettu += 1
        else:
            rows.append(row)

    if ohitettu:
        print(f"Varoitus: {ohitettu} dokumenttia ohitettu puuttuvien kenttien takia.", file=sys.stderr)

    if not rows:
        raise SystemExit("Ei migroitavia rivejä — lopetetaan.")

    write_to_mssql(args.mssql_connection_string, rows)


if __name__ == "__main__":
    main()
