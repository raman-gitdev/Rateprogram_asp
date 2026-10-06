# Tariff data review: B1C04, B1C05, B1C06

Reviewed 2026-10-05 against the source rate cards (`E:\EM6 Program\B1C04`, `B1C05`, `B1C06`) and the live
`ratehub` database (`tariff.ground_tariff`, `tariff.air_tariff`).

**Goal:** a user describes a shipment (from, to, weight, size, cargo) and gets comparable prices from every carrier
that can move it. Carriers price the same trip in different terms (city, postcode, province, carrier zone, carrier
station, named warehouse). Today the search only matches a lane when the user's question is in the *same* terms
as the lane. That is the main gap; the per-carrier data problems below make it worse.

Status legend: **Blocker** = wrong or missing prices today. **Gap** = carrier cannot be compared for some searches.
**Cleanup** = display or consistency.

---

## 1. Summary

| # | Carrier | Problem | Severity |
|---|---------|---------|----------|
| 1 | all | City / postcode / province searches never reach zone-priced lanes | Blocker |
| 2 | B1C04 | 21 of 31 rate cards not loaded; in the other 10 only the road "DD Economy" sheets are loaded | Blocker |
| 3 | B1C04 | Countries split by carrier station (`*1`, `*2`) produce 2–4 different prices for one shipment | Blocker |
| 4 | B1C05 | No zone chart at all: zip → zone (domestic) and country → zone (international) are missing | Blocker |
| 5 | B1C05 | Hundredweight bands have a gap (e.g. 226.343–226.795 kg finds no rate) | Blocker |
| 6 | B1C06 | 9 of 13 districts have no prefecture list in the file | Gap |
| 7 | B1C06 | Dangerous-goods rate table (table 2) not loaded | Gap |
| 8 | B1C06 | Origins are warehouses, stored as "city" with the shipper's name | Gap / confidentiality |
| 9 | all three | Surcharges, fuel, volumetric divisor and minimums missing (B1C04, B1C05) | Blocker for totals |
| 10 | all | Country shown as code; the same country is spelled 2–4 ways in the tables | Cleanup |
| 11 | B1C05 | Carrier name is part of a service code and of every lane code | Confidentiality |

---

## 2. The search problem (applies to every zone-priced carrier)

`Data/TariffRepository.cs` matches one lane end like this:

* user picks **city** → only lanes whose `*_point_type = 'CITY'` match;
* user picks **postcode** → only `POSTCODE` lanes (prefix match);
* user picks **province / region** → only `PROVINCE` / `REGION` lanes;
* user gives **only a country** → `COUNTRY` lanes, plus `ZONE` lanes whose carrier has a `ZONE_MAP` row for that country.

So a user who searches *Brussels → Manchester* or *zip 94043 → zip 10001* or *Tokyo → Fukuoka* gets **no** B1C04,
B1C05 or B1C06 price, even though all three carriers price those trips. The only way to see them is to leave the
place empty, and then the zone carriers that split a country (problem 3) return several prices at once.

### What the search should do instead

Ask for the address once, then translate it for each carrier:

```
user input:  country + (postcode | city | state)          e.g. GB, "M1 1AA"
                   │
                   ├─ master.country_alias            "United Kingdom" / "UK" / "GB"   -> GB
                   ├─ master.postcode_area            M1 -> England, Manchester        (national post data)
                   ├─ master.subdivision(_alias)      "Tokyo", "Guangdong Sheng"      -> JP-13, CN-GD
                   │
                   for each carrier lane end:
                   ├─ CITY / POSTCODE / PROVINCE lane  -> compare directly (as today)
                   ├─ ZONE lane, zone_basis COUNTRY    -> zone_member(country)                    (B1C04 most countries)
                   ├─ ZONE lane, zone_basis SERVICE_AREA -> station_of_postcode -> service_group -> zone   (B1C04 GB, DE, FR, IT, US, CN, MY, RU)
                   ├─ ZONE lane, zone_basis SUBDIVISION -> zone_member(subdivision)              (B1C06 districts)
                   ├─ ZONE lane, zone_basis POSTCODE_PAIR -> postal_zone_chart(origin zip, dest zip) (B1C05 US)
                   └─ LOCATION lane (warehouse)        -> offered as a named "from" place     (B1C06)
                   │
                   zone (+ zone_matrix for two-sided cards) -> FREIGHT rows -> price
```

Rules the search must keep:

1. **Never guess a zone.** If the address cannot be placed (no postcode for a station-split country, no zone chart),
   show the carrier with *"needs postcode"* / *"zone chart missing"* instead of a price, and never show two prices
   for one carrier/service as if both applied.
2. **Precision ladder.** Postcode beats city beats state beats country. Ask for the postcode when any carrier on the
   route needs it.
3. **Totals only in one currency.** Already the rule today; keep it.

`db/master/001_master_schema.sql` implements the tables and the lookup `master.zone_of(scheme, country, postcode,
city, subdivision)` which follows these rules (it returns NULL instead of guessing).

---

## 3. B1C04 (international express, 31 rate cards)

Rate card families: `AM_V01` (US, CA), `AM_USMain_V01` (US), `AP_V02` (CN, HK, IN, JP, KR, MY, PH, SG, TH, TW, VN),
`AP_LCY_V02` (CN, JP, KR, TH, TW, local currency), `EMEA_V01` (BE, DE, FI, FR, GB, IE, IL, IT, NL, PL),
`EMEA_ILSdom_V01` (IL), plus one Singapore domestic card (`SG DOM Rate Card_2026`).

### 3.1 Not loaded (Blocker)

* **Loaded:** only the 10 `EMEA_V01` cards, and from them only `DD Exp Economy`, `DD Imp Economy` and the
  `Zones DDI` sheets (service `ECONOMY_SELECT`, ground table, 9,299 rows).
* **Not loaded from those 10 cards:** `TD Exp WW`, `TD Imp WW`, `TD 3rdCty WW`, `TD Dom` / `TD Domestic`,
  `TD 3rdCty DOMESTIC`, the `Matrix` sheets, `DD 3rdCty Economy` + `Matrix DD 3rdCtry`, `S&S Published`,
  `S&S Special Agreement`, `IL Taxi Service Fee`, the incentive sheet, `DSX` (demand surcharge).
* **Not loaded at all:** the other 21 cards (all of Americas and Asia-Pacific, the IL domestic card, the SG domestic
  card).
* `air_tariff` has **no** B1C04 rows. The time-definite express products belong there.
* The TW cards carry each sheet twice (`... (VAT)` and without). Decide which one is the contract price; load one and
  record the other as a tax rule, not as a second price.
* `AP_V02` and `AP_LCY_V02` exist for the same countries (CN, JP, KR, TH, TW): one is in USD, the other in local
  currency. Load both only if the user should see both; otherwise pick one per country.

### 3.2 Station-split countries give several prices (Blocker)

Zone sheets split some countries into groups of carrier stations:

```
United Kingdom (GB) *1 | Zone 3          United Kingdom (GB) *1 | Rest of United Kingdom (GB)
United Kingdom (GB) *2 | Zone 5          United Kingdom (GB) *2 | Aberdeen (ABZ), Birmingham (BHX), ... (28 stations)
```

* Seen for GB, DE, FR, IT, RU (EMEA cards) and US, CN, MY (AP / AM cards). 158 stations in total
  (`db/master/data/carriers/B1C04_service_areas.json`).
* **The meaning of `*1` changes per card.** On the BE card GB `*1` = rest of UK; on the FR card GB `*1` = Belfast and
  Londonderry; on the DE card GB has four groups. So groups must be stored per rate card (`master.service_group`),
  never per country.
* The loader wrote **one ZONE_MAP row per group with no group key**, so the search cannot tell them apart. Example:
  BE → GB, 10 kg returns two B1C04 prices (Zone 3 and Zone 5). 24 country pairs are affected in the ground table.
* **The files do not say which postcodes belong to which station.** That list must come from the carrier
  (`master.service_area_postcode`). Until then, the right behaviour is to price the "rest of country" group only
  when the user confirms the address is not near a listed station, or to show "needs postcode".

### 3.3 Two-sided zoning (matrix) not modelled

Asia-Pacific and Americas cards price **origin zone × destination zone → rate column** (e.g. MY export: local zone
1/2 × international zone 1–11 → column A–V; US: local zones 1/2/3). The schema already has a `ZONE_MATRIX` record
type in `air_tariff`, but nothing is loaded. `master.zone_matrix` holds it in the master design.

### 3.4 Other B1C04 points

* Weight above the band table is an adder rule (`RULE / ADDER`, 319 rows: "per additional 1 kg" from 30.1 kg and
  "per additional 5 kg" from 70.1 kg), while the loaded weight bands run to 100 kg. Check on the card which applies
  between 30 and 100 kg. The search reports adders as "adder applies", not priced; it should compute
  `top band + ceil((kg − top) / step) × adder`.
* Volumetric divisor is stated in `S&S Special Agreement` → *VOLUMETRIC WEIGHT* and is not loaded
  (`volumetric_kg_per_cbm` is NULL on all B1C04 rows), so large light parcels are under-priced.
* Fuel surcharge is "variable, see carrier website": needs a monthly `FUEL_RULE` value, otherwise totals stay
  incomplete.
* Country codes on the cards that are not ISO: `IC`, `KV`, `XB`, `XC`, `XE`, `XM`, `XN`, `XS`, `XY`
  (`db/master/data/carrier_country_codes.json` maps each to its ISO country).

---

## 4. B1C05 (US parcel, 1 workbook, 42 sheets)

Loaded: ground table 5,499 rows (ground commercial / residential, 3-day, hundredweight, standard to / from Canada);
air table 25,339 rows (next day, 2nd day, worldwide express / saver / expedited / freight). Nothing else.

### 4.1 No zone chart (Blocker)

* Every sheet is priced by **zone number** (`002`–`008`, `044`–`046` domestic; `302`–`308` 3-day; `051`–`056` to
  Canada; `376`/`378`/`380` from Canada; `401`–`484` international).
* The workbook has **no zone chart**: nothing maps a US origin ZIP + destination ZIP to `002`–`008`, a Canadian
  postcode to `051`–`056`, or a country to `401`–`484`. The tariff rows therefore have only the country (US) and a
  zone. The search can only answer B1C05 when no place is given, and then returns every zone.
* Needed from the carrier (or its published zone charts): the zone chart for **each origin ZIP3 the shipper uses**
  (`master.postal_zone_chart`), the Canada zone chart, and the international country → zone list (load like
  B1C04 ZONE_MAP rows, `zone_basis = COUNTRY`).

### 4.2 Weight bands converted from pounds leave gaps (Blocker)

Sheets are in **pounds**. Package bands (1 lb, 2 lb …) were converted cleanly. Hundredweight bands were not:

| Band on the card | Stored as kg | Gap |
|---|---|---|
| 100–499 lb | 45.358 – 226.343 | |
| 500–999 lb | 226.795 – 453.139 | 226.343 – 226.795 kg finds nothing |
| 1000 lb and up | 453.591 – | 453.139 – 453.591 kg finds nothing |

Fix: store the band edges as `499 < lb ≤ 500` converted exactly (from = previous band's *to*), or keep the band in
pounds and convert the shipment instead (`master.carrier_pricing_rule` `WEIGHT_UNIT = LB`; carriers bill whole
pounds, rounded up).

### 4.3 Other B1C05 points

* No surcharges, fuel, residential / delivery-area surcharges or dimensional (DIM) divisor in the file. Totals are
  freight only. `MinimumsOrig` sheet is empty (header only).
* Hundredweight rates are per pound in the file; stored per kg (× 2.2046). Correct, but only valid once the
  minimum-weight rule (100 or 200 lb) is enforced.

---

## 5. B1C06 (Japan domestic road, 3 sheets)

Loaded: 473 ground rows (groupage from two warehouses to 13 districts, charter by distance, accessorials).

* **District → prefecture list incomplete (Gap).** The card prices to 13 districts but lists prefectures only for
  Tohoku A/B and Kyushu A/B (26 ZONE_MAP rows). The other 9 (Hokkaido, Kanto, Shinetsu, Tokai, Hokuriku, Kansai,
  Chugoku, Shikoku, Okinawa) are filled in `db/master/data/carriers/B1C06.json` from the usual Japanese grouping and
  marked `ASSUMED`. **Yamanashi** is the doubtful one (Kanto or Shinetsu). Confirm all 9 with the carrier.
* **ZONE_MAP uses PROVINCE but freight uses REGION**, so a province search never reaches B1C06 even for the 4 listed
  districts. With `master.zone_of` the search goes prefecture → district → freight.
* **Dangerous-goods table not loaded.** Each "Consolidated" sheet has a second table ("2. Depart From … (Dangerous
  goods)", 8 bands up to 30 kg). Only table 1 is in the database; DG shipments are priced at the general rate.
* **Origins are warehouses.** `origin_city` holds the warehouse name, which includes the shipper's name (439 rows), and
  `service_name` too (390 rows). They show in the "Place" dropdown. Store them as `master.carrier_location` codes
  (`JP-WH-HEIWAJIMA`, `JP-WH-NARASHINO`, `JP-WH-TSUKUBA`) and add the shipper name to `TariffHub.RedactTerms`.
* Narashino and Tsukuba share one rate table: one lane, two possible origins.
* Insurance (5 JPY per 10,000 JPY of declared value) and the consumption-tax note are not loaded (`PCT_OF_VALUE`).
* Charter (FTL) has no destination: priced by distance from the warehouse. The search needs a distance input or a
  distance lookup (prefecture-to-prefecture table) to compare it.

---

## 6. Cross-cutting

### 6.1 Countries

* Filters show `CZ`, `GB` … The same country is stored 2–4 ways (`Czech Republic`, `Czech Rep., The`,
  `CZECH REPUBLIC`, `Czech Republic, The`; `Ireland`, `Ireland, Rep. Of`, `Ireland, Republic Of`, `IRELAND`).
* Fix: show `master.country.name` ("Czechia (CZ)") via `master.v_country_option`; loaders map printed names with
  `master.country_code_of()`. All 1,018 known spellings resolve (checked against both tariff tables).

### 6.2 Filter values

Every coded value now has a label in `master.code_list` (service, piece type, cargo, lane type, equipment, OOG class,
temperature, charge basis), except one B1C05 service code that contains the carrier name (see 6.4).

### 6.3 Totals

| Carrier | Freight | Surcharges | Fuel | Volumetric | Minimum |
|---|---|---|---|---|---|
| B1C04 | EMEA road only | not loaded (`S&S` sheets) | variable, not loaded | in file, not loaded | per band |
| B1C05 | yes (zone needed) | not in file | not in file | not in file | sheet empty |
| B1C06 | yes | accessorials loaded | not stated | 166.67 kg/cbm loaded | n/a |

A comparison is only fair when each total says what it leaves out. Show "freight only" / "excl. fuel" next to totals
that are incomplete (open decision from the last session).

### 6.4 Confidentiality

* B1C05's carrier name is inside one `service_type` (3,050 rows), every `service_name` and every `lane_code`
  (30,838 rows across both tables). The redactor masks them, but codes should be neutral: rename the service to
  `GROUND` and rebuild lane codes from carrier code + service + zone.
* B1C04 carrier and product names appear in the rate cards; none are in the database today. Add the brand to
  `TariffHub.RedactTerms` **before** loading more B1C04 sheets (remarks are copied from the cards).
* The shipper's name is in B1C04 file names (`source_ref`) and in B1C06 `origin_city` / `service_name`.

---

## 7. What is in this project now

| Path | What |
|---|---|
| `db/master/001_master_schema.sql` | Schema `master`: countries, aliases, subdivisions, cities, postcode areas, carrier locations, stations, zone schemes / zones / members, service groups, zone matrix, postal zone charts, pricing rules, code lists, currencies, units, and lookup functions |
| `db/master/002_master_seed.sql` | Generated seed: 250 countries, 1,018 country spellings, 9 carrier-only country codes, 5,046 subdivisions + aliases, 178 currencies, 124 filter labels, 158 B1C04 stations, B1C06 warehouses and 13-district scheme (47 prefectures) |
| `db/master/data/*.json` | Same data as JSON (countries, subdivisions, aliases, currencies, code lists, units) |
| `db/master/data/carriers/B1C04_service_areas.json` | Stations per country and the `*n` groups per rate card and sheet |
| `db/master/data/carriers/B1C06.json` | Districts → prefectures (FILE / ASSUMED), warehouses |
| `db/master/tools/extract_rate_card_geography.py` | Re-reads the B1C04 cards → the two rate-card JSON files |
| `db/master/tools/build_master.py` | JSON (+ `pycountry`) → generated JSON and `002_master_seed.sql` |

Both SQL files were run against `ratehub` inside a transaction and **rolled back** (nothing is installed yet). Checks
that passed: every country code and name in both tariff tables resolves; every CN province name resolves;
`zone_of` gives Fukuoka → Kyushu A and Tokyo → Kanto for B1C06.

**Not seeded yet (data must come from outside the rate cards):** `postcode_area` (national post data),
`service_area_postcode` (B1C04), `postal_zone_chart` (B1C05), B1C04 zone schemes / matrix (to be created by the
loader when the missing sheets are loaded), `city` (to be filled from the tariff tables plus a city list).

---

## 8. Decisions to discuss

1. **Address input:** one "from" and one "to" box (country + postcode/city/state, with autocomplete from the master),
   instead of today's "kind of place" + free text?
2. **Unplaceable carriers:** show them as "needs postcode" / "zone chart missing" rows, or hide them?
3. **B1C04 scope:** load all 31 cards (TD express into `air_tariff`, matrix sheets, S&S, DSX) or only the countries the
   shipper ships from?
4. **TW VAT / AP vs AP_LCY duplicates:** which price is the contract price?
5. **B1C05 zone charts:** which origin ZIP codes does the shipper ship from? (one chart per origin ZIP3)
6. **B1C06:** confirm the 9 assumed districts (especially Yamanashi) and the DG warehouse address.
7. **Incomplete totals:** label them ("freight only", "excl. fuel") or exclude them from cheapest-first ordering?
