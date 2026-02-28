# LlmGateway — Esittely

LlmGateway on demo siitä, miten tekoäly voidaan kytkeä oikeaan tietokantaan niin, että tietokannalle voi esittää kysymyksiä **tavallisella suomen kielellä**.

---

## Ongelma jonka se ratkaisee

Perinteisesti tietokannasta saa tietoa vain lomakkeiden, hakukenttien tai SQL-kyselyjen kautta. Käyttäjän täytyy tietää mitä haluaa etsiä — ja miten se ilmaistaan järjestelmälle.

LlmGateway kääntää tämän toisin päin: **käyttäjä kysyy, järjestelmä selvittää itse miten vastaus haetaan.**

---

## Mitä se tekee käytännössä

Demossa on Suomen kaupunkien lämpötiladataa. Käyttäjä lähettää esim. tekstiviestin — gateway päättää tarvitaanko dataa kannasta, hakee sen tarvittaessa, ja palauttaa luettavan vastauksen.

**Esimerkki 1 — kysymys joka vaatii dataa:**

```
Kysymys:  "Mikä oli kylmin kuukausi Tampereella vuonna 2024?"

Taustalla: gateway tunnistaa datakysymyksen → LLM muodostaa SQL-kyselyn
           → Cosmos DB palauttaa tuloksen → LLM muotoilee vastauksen

Vastaus:  "Tampereen kylmin kuukausi vuonna 2024 oli tammikuu,
           jolloin keskilämpötila oli -8.4°C."
```

**Esimerkki 2 — kysymys joka ei vaadi dataa:**

```
Kysymys:  "Miksi lämpötila laskee talvella?"

Taustalla: LLM tunnistaa selityskysymyksen → vastaa suoraan omasta tiedostaan

Vastaus:  "Talvella aurinko paistaa matalammalta ja lyhyemmän ajan..."
```

**Esimerkki 3 — aiheen ulkopuolinen kysymys:**

```
Kysymys:  "Kirjoita minulle resepti"

Vastaus:  "Pystyn vastaamaan vain Suomen kaupunkien säähän
           ja lämpötiloihin liittyviin kysymyksiin."
```

---

## Kolme tapaa vastata

Gateway valitsee automaattisesti sopivimman lähestymistavan kysymyksen perusteella:

| Tilanne | Lähestymistapa | Esimerkki |
|---------|---------------|-----------|
| Tarkka luku tai laskutulos | Haku tietokannasta SQL:llä | "Mikä oli keskilämpötila helmikuussa?" |
| Selitys tai konteksti | LLM vastaa suoraan | "Miksi talvi on kylmä?" |
| Tekstikuvaus tai tunnelma | Semanttinen haku dokumenteista | "Miltä Helsingin talvi tuntui?" |

Sama tekstikenttä — eri vastausstrategia konepellin alla.

---

## Mikä tässä on uutta

Tietokantaan kyseleminen luonnollisella kielellä ei ole uusi idea. LlmGateway demonstroi miten se tehdään **luotettavasti**:

- LLM ei voi tuhota dataa — kirjoitusoperaatiot on estetty koodissa, ei pelkästään ohjeistamalla LLM:ää
- Jos Azure-yhteys katkeaa, palvelu antaa selkeän virheviestin eikä jää odottamaan loputtomiin
- Kalliimpi malli otetaan käyttöön vain silloin kun kevyempi ei riitä
- Token-kulutus ja kustannukset pysyvät hallinnassa rajoitetuilla kierroksilla ja aikakatkaisuilla

---

## Tekniikka lyhyesti

- **Kieli:** C# / .NET 10
- **Tekoäly:** Azure OpenAI (GPT-4o ja GPT-4o-mini)
- **Tietokanta:** Azure Cosmos DB
- **Rajapinta:** yksinkertainen REST API — yksi endpoint, yksi JSON-viesti

---

## Lisää tietoa

| Dokumentti | Sisältö |
|------------|---------|
| [README.md](README.md) | Policyit, testauskomennot |
| [FAQ.md](FAQ.md) | Tekniset kysymykset: virhetilanteet, tietoturva, kustannukset |
| [INFRA.md](INFRA.md) | Azure-infrastruktuuri ja käyttöönotto |
| [Terms.md](Terms.md) | Termistö ja käsitteet |
