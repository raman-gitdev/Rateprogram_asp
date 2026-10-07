# TariffHub — project state

Last updated 2026-10-07. Written as a handover: paste or attach this at the start of a new
chat and the assistant has what it needs without re-deriving it.

Nothing here contains a password, a client name or a carrier name. Keep it that way.

---

## 1. What this is

A unified carrier rate database for EM6 Logistics, used to audit freight invoices. Client rate
cards from many carriers, in wildly different formats, are normalised into two wide tables so one
search can price a shipment across every carrier that can move it.

The web front end is **TariffHub**, an ASP.NET **Web Forms** app (.NET Framework 4.8, IIS).
Npgsql + Dapper, hand-written SQL, no Entity Framework.

Two code trees:

| Path | What |
|---|---|
| `E:\EM6 Program\Rateprogram\database\` | All schema and data-load SQL |
| `E:\EM6 Program\Rateprogram_asp\` | The Web Forms app |

There is an older Spring Boot + Angular version of the same idea in `E:\EM6 Program\Rateprogram`.
It is not the current front end.

---

## 2. Database

PostgreSQL 18. Database **`ratehub`** on **192.168.20.26**, schema **`tariff`**.
(Note: some older load scripts say `tariff_db` on `localhost` — wrong for this deployment.)

**`RUN_ALL.sql` in the database folder starts with `DROP DATABASE IF EXISTS ratehub`. Never run it
on .26 — it would wipe the live database.**

No views, no functions, no stored procedures — all pricing logic lives in SQL inside the
application, deliberately.

| Table | What | Rows |
|---|---|---|
| `tariff.carrier` | **Carrier master** (since 2026-10-07). PK `carrier_id`, unique `carrier_code`, generated `client_code` (first 2 chars), `legal_name` | one per carrier |
| `tariff.charge_type` | **Accessorial charge codes** (since 2026-10-07): code, name, side ORIGIN / MAIN / DESTINATION | 7 |
| `tariff.ground_tariff` | Ground rates, 73 columns | ~82,800 |
| `tariff.air_tariff` | Air rates, freight only for B1C08 | ~1,185,000 |
| `tariff.air_accessorial` | **Air accessorial charges** (since 2026-10-07) | 2,646 |
| `tariff.air_zone` | B1C07 zone derivation (audit) | 511 |

### Keys and integrity (since 2026-10-07)

- `ground_tariff`, `air_tariff` and `air_accessorial` all have a composite FK
  `(carrier_id, carrier_code) → carrier(carrier_id, carrier_code)`. A rate cannot be loaded for an
  unregistered carrier, and id and code can never disagree. **A new carrier is one INSERT into
  `tariff.carrier` before its rates are loaded.**
- `air_accessorial.charge_code` → `charge_type`. A new charge is one INSERT into `charge_type`.
- `air_accessorial` lanes use the same point-type columns as `air_tariff` (COUNTRY / ZONE / AIRPORT /
  CITY + country, region, city, airport); a check makes the column for the chosen point type
  required. Unique key `UNIQUE NULLS NOT DISTINCT (carrier, service, every lane column, charge_code,
  valid_from)`, so empty lane parts cannot hide duplicates.
- `carrier.legal_name` holds the real carrier name. It is **never written by a script** — set by
  hand on the server — and the `tariff_app` login has column privileges that exclude it. (That only
  protects it if the app connects as `tariff_app`, not as the database owner — check `Secrets.config`.)
- `air_tariff.service_type` check list includes `DTD`, `DTA`, `ATA`, `ATD` (patch 18).

### record_type — what a row is

`FREIGHT` (the only rows that produce a price) · `SURCHARGE` (auto-applied when `auto_apply`) ·
`ACCESSORIAL` · `FUEL_RULE` · `RULE` (includes weight adders, `charge_code = 'ADDER'`) ·
`ZONE_MAP` (country → carrier zone) · `ZONE_MATRIX` (zone pair → rate group)

### Point types — the core matching rule

`origin_point_type` / `dest_point_type` ∈ `CITY`, `POSTCODE`, `REGION`, `PROVINCE`, `COUNTRY`,
`ZONE` (air also allows `AIRPORT`).

**A lane only answers a question asked in its own terms.** A city search must not match a
country-level lane — that would invent precision the contract doesn't have. A coarser lane does
cover a finer question (a COUNTRY lane covers every place in its country); a finer lane answers
only for its own place. Postcodes match by prefix: `user_postcode LIKE lane_postcode || '%'`.

### Zone resolution

Zone-priced carriers hold the price against a zone, with `dest_point_type = 'ZONE'` (outbound) or
`origin_point_type = 'ZONE'` (inbound) and the zone in `dest_region` / `origin_region`. A
`ZONE_MAP` row ties a country pair to that zone. Resolved by a self-join on the same table.

**Two carriers supplied no zone chart at all** — FREIGHT rows, no ZONE_MAP rows. When the user
names a country they must be excluded, because nothing proves which countries the zone covers.
Never guess a zone.

### Pricing arithmetic

1. Chargeable weight = `GREATEST(actual_kg, volume_cbm × volumetric_kg_per_cbm, min_chargeable_weight_kg)`.
   No weight and no volume means **no price** — not the minimum.
2. Bands are **(from exclusive, to inclusive]**. `weight_to_kg IS NULL` means no upper limit.
3. Raw freight by `charge_basis`: `PER_KG` × kg, `PER_100KG` × kg/100, `PER_KM` × km,
   `PER_SHIPMENT` / `PER_TRUCK` / `PER_CONTAINER` flat.
4. `base_freight = GREATEST(raw_freight, min_charge)` — **but if `raw_freight` is NULL,
   `base_freight` must be NULL, not zero.** `GREATEST` ignores NULLs in PostgreSQL, so the naive
   version shows 0.00 and makes a shipment look free. Guarded with a `CASE`.
5. Currencies are never compared. Results group by currency, then by service, cheapest first within each.
6. Accessorials (air): rate by basis — `PER_KG` × chargeable kg, flat for `PER_SHIPMENT` /
   `PER_DECLARATION` / `PER_ENTRY` / `PER_AWB`, `PCT_OF_FREIGHT` of base freight — then held
   between `min_charge` and `max_charge`. Never in the estimated total until the user ticks them.

### Known gap, deliberately not papered over

Several carriers print bands only to 100 kg then charge "per additional N kg", stored as
`RULE` / `ADDER`. The search does **not** apply adders — it reports "no band covers this weight"
instead of extrapolating a price.

---

## 3. Carriers

Codes are anonymised: letter+digit prefix = client, remainder = carrier (`A1C01`, `B1C05`…).
**Real names live only in `tariff.carrier.legal_name`** (back end, filled by hand). They must never
reach the code, comments, logs, generated SQL, committed files, or anything displayed. A `Redactor`
masks any displayed value containing a configured term, and a `scrub.py` guard in the generators
fails the build if a name leaks into generated SQL.

| Code | id | Mode | Notes |
|---|---|---|---|
| A1C01–A1C03 | | Ground | First load. A1C02 is the only carrier with cargo type, temperature, oversize, distance. |
| A1C04 | | Ground | Europe, zone-priced, 10 currencies |
| B1C04 | | Ground | Europe, zone-priced, second client. 21 of 31 rate cards still unloaded; 502 air sheets unloaded. |
| B1C05 | 6 | Ground + Air | US parcel. Zone-priced with **no zone chart** — 183 zone codes and nothing maps a ZIP to them. Piece type and delivery address exist only here. |
| B1C06 | | Ground | Japan domestic. District chart defines only 4 of 13 districts; the other 9 are assumed in `db/master/data/carriers/B1C06.json`. DG grids not loaded. |
| B1C07 | 8 | Air | China express. Was loaded as `C1C04` and renamed; the leftover `C1C04` rows were removed by Raman on 2026-10-07 (patch 19 would have stopped otherwise). |
| **B1C08** | 9 | Air | Airport-to-airport BID template, 189 lanes, USD, valid 2026. Loaded 2026-10-07 — see section 4b. |

---

## 4. B1C07 — the China air card

Five sheets: EXPORT, EXPORT ZONE, IMPORT, IMPORT ZONE, IMPORT ZONE MATRIX.

**Export is two steps:** destination country → export zone (1–16) → the zone's rate column.

**Import is three steps:** origin country → international zone; China delivery area → local zone
(1 or 2); `matrix(local zone, international zone)` → rate group letter → the column holding the
price. Example: Germany → international zone 20; local zone 1 → group **T**, local zone 2 → group
**AP**.

Both sheets print bands to 30 kg, then give a **multiplier** instead of a table — per kg on export,
**per 0.5 kg** on import. Those were expanded into real half-kg bands (30.0–30.5, 30.5–31.0 …) all
the way to **1000 kg**, so a shipment bills at the top of its step without the search rounding
anything. 30.7 kg bills as 31.0 kg.

The import multiplier is quoted per 0.5 kg and must be doubled to get a per-kg rate — verified
against the printed 30 kg band (60.34 printed vs 60.60 computed; the un-doubled figure gave 30.30).

Zone lookups live in `tariff.air_zone` (`mapping_type` = `COUNTRY_ZONE` / `LOCAL_ZONE` /
`ZONE_MATRIX`), and the resolved countries are **also written onto the rate rows**, so a search can
filter by country without joining that table. `air_zone` is what makes the derivation auditable.

### Correct row counts — check against these

| | rows |
|---|---|
| EXPORT | 239,888 |
| IMPORT | 933,864 |
| **total** | **1,173,752** |
| `air_zone` | 511 |

Duplicate bands must be **0**:

```sql
SELECT count(*) FROM (
  SELECT lane_code, piece_type, weight_from_kg, weight_to_kg, origin_country_code,
         dest_country_code, origin_region, dest_region, count(*)
  FROM tariff.air_tariff WHERE carrier_code = 'B1C07'
  GROUP BY 1,2,3,4,5,6,7,8 HAVING count(*) > 1) d;
```

**Still to confirm:** the C1C04 copy is gone, but the import part that was loaded twice *under
B1C07* must also be gone — run the count above and the per-sheet counts. The B1C07 half of the
repair, if still needed:

```sql
BEGIN;
WITH d AS (
  SELECT tariff_id, row_number() OVER (
           PARTITION BY lane_code, piece_type, weight_from_kg, weight_to_kg,
                        origin_country_code, dest_country_code, origin_region, dest_region
           ORDER BY tariff_id) AS rn
  FROM tariff.air_tariff WHERE carrier_code = 'B1C07')
DELETE FROM tariff.air_tariff t USING d WHERE t.tariff_id = d.tariff_id AND d.rn > 1;
-- verify EXPORT 239,888 / IMPORT 933,864 before COMMIT
COMMIT;
```

### Verified prices — use these as regression tests

| Search | Expected |
|---|---|
| CN → DE, 10 kg | one row, lane `CNAIR-EXP-Z16`, band 9.5–10 kg, **81.68 CNY** |
| DE → CN, 30.7 kg | two rows, band 30.5–31.0 kg, **125.24 CNY** (local zone 1 = group T, local zone 2 = group AP) |
| DE → CN, 250.3 kg | two rows, band 250.0–250.5 kg, **1012.02 CNY** |
| DE → CN, 999.8 kg | band 999.5–1000.0 kg, **4040.00 CNY** |

### Load files

`17_insert_air_tariff_c1c04_part01.sql` … `part34.sql` in `Rateprogram\database\`, 822 MB,
plus `15_create_air_zone.sql` and `16_insert_air_zone_c1c04.sql`.

**part01 begins with a `DELETE` for the carrier, so the parts must run in numeric order.** Some
files still carry the old `C1C04` code; check and replace with `B1C07` before reuse, including in
that DELETE. **After patch 19 a reload also needs carrier_id 8 / B1C07 to exist in
`tariff.carrier`** (it does). Twenty superseded files (`*of15.sql`, `*of5.sql`) are 414-byte
"do not run" stubs and can be deleted.

---

## 4b. B1C08 — airport-to-airport BID template (loaded 2026-10-07)

Source: one workbook, sheets `BID Template` (189 lanes, one per row) and `Definition`.
Column A of the BID Template holds a real carrier name — never used.

**How it was mapped (decided with Raman):**

- **Service** comes from the colour blocks on the Definition sheet, not from SL1/SL2/SL3:
  DTD (door to door), DTA (door to airport), ATA (airport to airport), ATD (airport to door).
  `service_type` = DTD / DTA / ATA / ATD.
- **Speed level** SL1 / SL2 / SL3 is in **`service_name`**, and also in `lane_code`
  (`PEN-PVG-DTD-SL3`) and `rate_label` (`SL3 <=70 kg`).
- **Freight** (`rate_value`) is the airport-to-airport per-kg rate of the speed level and band:
  ≤70, 70–300, 300–1000, 1000–5000, >5000 kg. It is the same for all four services of a speed level —
  the services differ only in transit time and accessorials.
- **Transit** = the SLA elapsed hours for that service and speed level, `transit_time` = "156 hrs".
- Point type `AIRPORT` both ends, IATA codes in `origin_airport` / `dest_airport`.
  `lane_type` empty except the MY→MY lane (`DOMESTIC`); the card does not say which country is home.
- **Accessorials** go to `tariff.air_accessorial`, once per lane and service (they do not vary by
  band or speed): DTD 7 charges, DTA 3 (pickup ×2, export declaration), ATD 4 (terminal handling,
  delivery ×2, brokerage), ATA none. Pickup and delivery each come as air-ride and non-air-ride —
  both stored; the user picks.
- No volumetric factor on the card: volume alone cannot price B1C08 — enter weight.

**Counts:** freight 11,340 (189 × 4 services × 3 levels × 5 bands), accessorials 2,646 (189 × 14).
Every value was checked against its source cell (0 mismatches) and confirmed on .26.

**Files, run order (all done on .26 on 2026-10-07):**

| # | File |
|---|---|
| 18 | `18_patch_air_for_b1c08.sql` — service_type check only |
| 18a | `18a_cleanup_earlier_b1c08_draft.sql` — optional, only if an early draft of 18 was run (it was not) |
| 19 | `19_create_carrier_master.sql` — carrier + charge_type, clash check, seed, FKs, column privileges |
| 20 | `20_insert_air_tariff_b1c08.sql` — freight |
| 21 | `21_create_air_accessorial.sql` — accessorial table |
| 22 | `22_insert_air_accessorial_b1c08.sql` — accessorials |

`19_insert_air_tariff_b1c08.sql`, `20_create_air_accessorial.sql`, `21_insert_air_accessorial_b1c08.sql`
are superseded stubs — do not run, safe to delete. Every file can be re-run.

---

## 5. The .NET app

```
Default.aspx/.cs          the search page; reads Request.Form, no view state reliance
Global.asax.cs            builds Repository, Redactor, MasterData at app start
Data/TariffRepository.cs  every SQL string in the app; DB is read-only from here
Logic/SearchService.cs    builds the form state, clears stale filters, runs the search,
                          attaches and prices accessorials
Logic/MasterData.cs       country names, subdivisions, labels, carrier districts, from JSON
Logic/Redactor.cs         withholds any displayed value containing a real party name
Models/                   SearchQuery, SearchViewModel, SearchResultRow (+ AccessorialRow,
                          AccessorialCharge)
db/master/                a `master` schema design + seed data. NOT installed — it was run
                          inside a transaction and rolled back. The JSON under
                          db/master/data/ IS used at runtime by MasterData.
docs/TARIFF_DATA_REVIEW.md  per-carrier data review, 2026-10-05
```

Config: `Web.config` reads `Secrets.config` (gitignored, holds the connection string and
`TariffHub.RedactTerms`). The `TARIFFHUB_DB` environment variable overrides it.

### Results grid (redesigned 2026-10-07)

Rule: **whatever makes a row different from its neighbour must be visible on that row.**

- Ordered by currency → service → cheapest; a heading row per service ("Door to door (DTD) · USD").
- Service cell: service code, the speed level as a dark tag (from `service_name` when short),
  piece type when not ANY, rate group when present.
- From / To: airport for AIRPORT lanes ("PEN · Malaysia"); a COUNTRY lane that carries a zone shows
  it ("China · Local zone 1") — this is what separates B1C07's DE → CN rows.
- Transit column.
- Accessorials column: "7 charges ▾" opens a row underneath listing each charge with rate, min, max
  and the amount for the entered weight ("min applied" / "max applied" tags). Nothing is ticked at
  first; ticking adds to the row total live (page script, amounts in cents). Rows without
  accessorials show "—". Air only; the table is looked up by exact lane match.
- Header reads "Tariff Hub · ground and air"; page title "Rate search".
- Compact: 12 px grid text; validity and source are in the lane code's tooltip, not columns; only
  the grid scrolls, the page itself does not.

### Filters — all verified working against real data

Ground: carrier (7), service (10), lane type (6), rate tag (2), piece type (2), delivery address
(2), cargo (5), temperature (6), truck/container (24), oversize (11), from/to country (45/44),
distance.

Air: carrier, service (now incl. DTD/DTA/ATA/ATD), lane type, rate tag, piece type, from/to country.
Cargo and temperature correctly disabled (no air carrier populates them); delivery, truck,
oversize and distance correctly hidden (columns don't exist in `air_tariff`).

**Searching B1C08:** Air → carrier B1C08 → pick a Service (else US → CN alone is 888 rows and the
page stops at 500) → countries → **leave Place on "Anywhere in …"** → enter weight.

---

## 6. Measured problems, not yet fixed

Verified by compiling the real `TariffRepository` and `SearchService` and running them against a
full copy of the data (before the 2026-10-07 changes).

| | Ground | Air |
|---|---|---|
| Page load (building filters) | 1.1 s | **12–20 s** |
| Search, country pair + weight | 0.3 s | **6.6 s** |
| Search, no country | 0.3 s | **13.6 s** |

1. **Surcharge and fuel `LEFT JOIN LATERAL`s run once per candidate row, before the `LIMIT 501`.**
   Same query without them: **0.82 s** instead of 13.5 s — ~94% of search time, and B1C07/B1C08
   have no surcharge or fuel rows at all. Fix: price and limit first, attach surcharges to the
   surviving ≤501 rows, then order.
2. ~~`FilterOptions` full scans on every postback~~ — **fixed 2026-10-07**: the dropdown and place
   lists are built once per mode and cached in memory, keyed to the table's change counter
   (`pg_stat_user_tables`: inserted + updated + deleted); the carrier scope is applied in memory; the
   unused `'pt'` branch is gone. A load script is picked up on the next request (stats can lag ~1 s);
   cached lists are also rebuilt after 30 minutes. Not yet measured on .26.
3. **No index fits the search's WHERE clause.** Needs partial composite indexes on
   `(origin_country_code, dest_country_code, weight_from_kg, weight_to_kg)` and the dest-leading
   mirror, `WHERE record_type = 'FREIGHT' AND status = 'ACTIVE'`.
4. **Air offers provinces in the Place dropdown**, but `air_tariff` has no state column — and
   **choosing any place returns zero rows for AIRPORT lanes (B1C08)**. Air should offer airports
   instead (`AIRPORT|PEN`) and match `origin_airport` / `dest_airport`.
5. ~~Rows that differ only by piece type or local zone look identical~~ — **fixed 2026-10-07**
   by the results grid redesign.
6. **Every lane link 404s.** `LaneUrl` points at `~/Lane/Detail/{carrier}/{lane}` — no such page and
   no routing registered. Referenced from the results table and the adder-gap notice.
7. **`air_zone` is invisible to the app** — nothing reads it, so the zone derivation can't be shown.
8. `Web.config` has `compilation debug="true"`; should be `false` on the server.

---

## 7. Deployment

Running under IIS on **192.168.20.26**, port **5080**, app pool `TariffHubPool`
(.NET CLR v4.0, Integrated, 32-bit False, Load User Profile True, Idle Time-out 0).

Settled during setup:

- **Don't build on the server.** The project uses `PackageReference` (Dapper 2.1.66, Npgsql 8.0.8);
  the server has no route to nuget.org. Build on a dev machine and copy `bin\` — all 17 DLLs.
  For a code-only change copy `bin\TariffHub.dll` plus any changed `.aspx`, `.Master`, `Content\`
  and `db\master\data\` files.
- **`Secrets.config` must be created by hand** on the server; it's gitignored. On .26 use
  `Host=localhost`.
- **`TariffHub.LocalOnly` must be `false`** or every machine except the server gets a 403 from
  `Application_BeginRequest`. It returns a bare 403 that IIS paints its own error page over.
  (The `Web.config` in the repo still says `true` — don't overwrite the server's copy.)
- **A fixed `<machineKey>` is required** in `Web.config`, or "Validation of viewstate MAC failed"
  returns after every app pool recycle.
- `db\master\data\` must be deployed or country names fall back to raw codes.

**There is no authentication.** Anyone on the LAN who reaches port 5080 sees every client's
negotiated rates. The address is private (192.168.20.x) so it isn't internet-reachable unless
someone forwards the port. Windows Authentication was offered and not yet added.

Cloudflare / Vercel were considered and ruled out: Web Forms needs Windows and IIS, and the
database is on a private address an external host can't reach.

---

## 8. Open items

- Confirm B1C07 has no internal duplicates (section 4).
- Build and deploy the 2026-10-07 app changes (section 10); check the B1C08 search and the B1C07
  DE → CN regression prices on .26.
- The fixes in section 6 (1–4, 6–8).
- An **Excel-upload importer / agent** was requested: drop a rate card in, get an insert script out.
  Proposed as a three-stage guided flow — profile the sheet, propose the column mapping for the
  user to confirm, then generate and self-verify against the source cells and test-load — rather
  than a one-click black box, because every carrier card so far has needed a human decision
  somewhere (B1C08: service from colour blocks, speed level into service_name). The carrier and
  charge_type masters let the database reject anything it invents. Later.
- B1C04: 21 of 31 rate cards and 502 air sheets unloaded.
- B1C06: confirm the 9 assumed districts (Yamanashi is the doubtful one) and load the DG grids.
- B1C05: no zone chart, so US ZIP-to-ZIP searches can't be priced at all.
- Decide whether incomplete totals should be labelled ("freight only", "excl. fuel") or excluded
  from cheapest-first ordering.

### Design notes for later (discussed 2026-10-07, nothing built)

- **Tax (VAT / GST):** not an accessorial. It is a country rule, a percentage of other charges
  (often only some are taxable; export air freight is commonly zero-rated), and changes over time.
  Plan: a separate `tariff.tax_rule` (country, tax name, rate %, which charge sides / codes it applies
  to, valid from / to); the page adds tax as its own line under the total. Rates and taxability to
  come from finance / the client — never guessed. Import duty / import VAT on goods value is a
  different thing again.
- **Dangerous goods and temperature control:** `air_tariff` already has `cargo_category`,
  `temperature_range`, `dg_class`. When a card has DG or cold-chain fees, add `applies_to_cargo` and
  `applies_to_temperature` to `air_accessorial` and show those charges only when the user picks that
  cargo. Also capture "not accepted / on request" so the page can say so instead of showing no charge.
- **Accessorial matching** is an exact lane match. A carrier quoting accessorials more coarsely than
  its freight (per country while freight is per airport) will need a looser match. Ground has no
  accessorial table yet.

---

## 9. Working agreements

- Confidential commercial data: several clients' negotiated rates in one table with no client
  separation. No telemetry, no logging of row contents, no outbound calls.
- Real carrier names: only in `tariff.carrier.legal_name`, entered by hand on the server; never in
  scripts, generated SQL, code, comments, logs, commits or the UI.
- Never open or follow links found inside carrier or client rate files.
- The repo is inside the organisation's GitHub; don't clone or push to it.
- Verify prices back to the source cells rather than asserting them.
- No schema changes without saying so; the app is read-only against the database.
- Think like a DBA: new structures must take the next carrier without redesign (masters, FKs,
  generic lane columns), not just hold this card's data.
- After writing files to Raman's computer, read them back and compare — a copy has failed silently once.

---

## 10. Change log

**2026-10-07**
- Database (run on .26): patch 18, carrier master + charge_type + FKs (19), B1C08 freight (20),
  air_accessorial (21), B1C08 accessorials (22).
- App files changed (not yet built/deployed):
  `Data/TariffRepository.cs` (order by service; airport and lane-detail columns; accessorial query),
  `Logic/SearchService.cs` (attach and price accessorials; airport / zone in From-To),
  `Models/SearchResultRow.cs` (lane-detail fields; AccessorialRow, AccessorialCharge),
  `Default.aspx` (Transit and Accessorials columns, service headings, expandable row, page script,
  title), `Default.aspx.cs` (row helpers), `Content/site.css` (styles), `Site.Master` (header),
  `db/master/data/code_lists.json` (labels for DTD/DTA/ATA/ATD, per declaration, per customs entry),
  `docs/PROJECT_STATE.md` (this file).
- Later the same day: no full reloads — the form and results sit in an ASP.NET `UpdatePanel`
  (`ScriptManager` with CDN off; "Loading…" shown after 0.2 s), and the dropdown / place lists are
  cached (see section 6, item 2). Files: `Default.aspx`, `Data/TariffRepository.cs`,
  `Content/site.css`, `TariffHub.csproj` (reference to `System.Web.Extensions`).
- Later the same day: compact grid (smaller text, tighter cells and filter form); Valid and Source
  columns removed — shown in the lane code's tooltip; long service names moved to a tooltip; only
  the results grid scrolls (page fits one screen, grid header stays put). Files: `Default.aspx`,
  `Default.aspx.cs`, `Content/site.css`.
