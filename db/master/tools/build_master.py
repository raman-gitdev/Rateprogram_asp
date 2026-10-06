"""Build the master JSON files and db/master/002_master_seed.sql.

Inputs (hand-maintained, in db/master/data):
  code_lists.json, carrier_country_codes.json, carriers/B1C06.json,
  rate_card_country_spellings.json and carriers/B1C04_service_areas.json (from extract_rate_card_geography.py)
Generated (db/master/data):
  countries.json, subdivisions.json, currencies.json, country_aliases.json, unit_conversions.json
and db/master/002_master_seed.sql.

Requires: pip install pycountry     Usage: python build_master.py
"""
import json
import os
import re
import unicodedata

import pycountry

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.normpath(os.path.join(HERE, '..', 'data'))
SEED = os.path.normpath(os.path.join(HERE, '..', '002_master_seed.sql'))


def load(name):
    with open(os.path.join(DATA, name), encoding='utf-8') as f:
        return json.load(f)


def save(name, obj):
    with open(os.path.join(DATA, name), 'w', encoding='utf-8') as f:
        json.dump(obj, f, ensure_ascii=False, indent=1)


def ascii_of(s):
    return unicodedata.normalize('NFKD', s).encode('ascii', 'ignore').decode()


def norm(s):
    return re.sub(r'\s+', ' ', s.replace('\xa0', ' ')).strip().upper()


# ---------------------------------------------------------------- countries
# Display names where the ISO short name reads badly in a dropdown.
NAME_OVERRIDE = {
    'BO': 'Bolivia', 'BQ': 'Bonaire, Sint Eustatius and Saba', 'CD': 'Congo (Democratic Republic)', 'CG': 'Congo',
    'FM': 'Micronesia', 'GB': 'United Kingdom', 'IR': 'Iran', 'KP': 'North Korea', 'KR': 'South Korea',
    'LA': 'Laos', 'MD': 'Moldova', 'PS': 'Palestine', 'RU': 'Russia', 'SY': 'Syria', 'TW': 'Taiwan',
    'TZ': 'Tanzania', 'VA': 'Vatican City', 'VE': 'Venezuela', 'VN': 'Vietnam', 'MK': 'North Macedonia',
    'US': 'United States', 'VG': 'British Virgin Islands', 'VI': 'U.S. Virgin Islands', 'HK': 'Hong Kong',
    'MO': 'Macao', 'BN': 'Brunei', 'CV': 'Cape Verde', 'SZ': 'Eswatini', 'TR': 'Türkiye', 'CZ': 'Czechia',
}

countries = []
for c in pycountry.countries:
    name = NAME_OVERRIDE.get(c.alpha_2) or getattr(c, 'common_name', None) or c.name
    countries.append({'country_code': c.alpha_2, 'iso3_code': c.alpha_3, 'iso_numeric': c.numeric,
                      'name': name, 'official_name': getattr(c, 'official_name', None) or c.name, 'is_iso': True})
countries.append({'country_code': 'XK', 'iso3_code': 'XKX', 'iso_numeric': None, 'name': 'Kosovo',
                  'official_name': 'Republic of Kosovo', 'is_iso': False})
countries.sort(key=lambda r: r['name'])
codes = {r['country_code'] for r in countries}
save('countries.json', {'_comment': 'ISO 3166-1 countries (from pycountry) plus XK Kosovo. name = what the filter shows.',
                        'countries': countries})

# ---------------------------------------------------------------- aliases
aliases = {}


def alias(text, code, source):
    if code in codes and text and norm(text) not in aliases:
        aliases[norm(text)] = {'alias': re.sub(r'\s+', ' ', text.replace('\xa0', ' ')).strip(), 'country_code': code, 'source': source}


for r in countries:
    alias(r['name'], r['country_code'], 'ISO')
    alias(r['official_name'], r['country_code'], 'ISO')
    alias(r['country_code'], r['country_code'], 'ISO')
    alias(r['iso3_code'], r['country_code'], 'ISO')
for c in pycountry.countries:
    alias(c.name, c.alpha_2, 'ISO')
    alias(getattr(c, 'common_name', None), c.alpha_2, 'ISO')

carrier_codes = load('carrier_country_codes.json')
printed = {row['carrier_code']: row['iso_code'] for rows in carrier_codes.values() if isinstance(rows, list) for row in rows}
for code, names in load('rate_card_country_spellings.json')['spellings'].items():
    for n in names:
        if n.lower().startswith('rest of'):
            continue
        alias(n, printed.get(code, code), 'RATE_CARD')

# Spellings seen in tariff.ground_tariff.origin_country_name / dest_country_name.
for text, code in [('Bosnia And Herzegovina', 'BA'), ('Bosnia & Herzegovina', 'BA'), ('Czech Republic', 'CZ'),
                   ('Czech Republic, The', 'CZ'), ('Ireland, Republic Of', 'IE'), ('Ireland, Rep. Of', 'IE'),
                   ('Netherlands, The', 'NL'), ('Serbia, Republic Of', 'RS'), ('Vatican City State', 'VA'),
                   ('Vatican City', 'VA'), ('Turkey', 'TR'), ('Holland', 'NL'), ('Great Britain', 'GB'), ('UK', 'GB'),
                   ('England', 'GB'), ('USA', 'US'), ('U.S.A.', 'US'), ('America', 'US'), ('Korea', 'KR'),
                   ('Republic of Korea', 'KR'), ('PRC', 'CN'), ("People's Republic of China", 'CN'),
                   ('Mainland China', 'CN'), ('UAE', 'AE'), ('Swaziland', 'SZ'), ('Burma', 'MM'), ('Ivory Coast', 'CI')]:
    alias(text, code, 'TARIFF_TABLE' if ',' in text or text in ('Turkey', 'Vatican City', 'Vatican City State',
                                                                 'Czech Republic', 'Bosnia And Herzegovina',
                                                                 'Bosnia & Herzegovina') else 'MANUAL')
alias_rows = [dict(alias_norm=k, **v) for k, v in sorted(aliases.items())]
save('country_aliases.json', {'_comment': 'Every known spelling -> ISO code. alias_norm = upper case, single spaces.',
                              'aliases': alias_rows})

# ---------------------------------------------------------------- subdivisions
CN_SUFFIX = re.compile(r'\s+(Sheng|Shi|Zizhiqu|Huizu Zizhiqu|Uygur Zizhiqu|Zhuangzu Zizhiqu|Zangzu Zizhiqu|'
                       r'Tebiexingzhengqu|Tebie Xingzhengqu)$')
subs = []
for s in pycountry.subdivisions:
    short = CN_SUFFIX.sub('', s.name) if s.country_code == 'CN' else s.name
    subs.append({'subdivision_code': s.code, 'country_code': s.country_code, 'name': s.name, 'short_name': short,
                 'ascii_name': ascii_of(short), 'type': s.type, 'parent_code': getattr(s, 'parent_code', None)})
subs.sort(key=lambda r: r['subdivision_code'])

# Subdivision spellings: ISO name, short name, ASCII form, and known variants (pinyin, English).
sub_aliases = {}


def sub_alias(text, code, source):
    if text and (norm(text), code) not in sub_aliases:
        sub_aliases[(norm(text), code)] = {'alias': text, 'subdivision_code': code, 'source': source}


for s in subs:
    for t in (s['name'], s['short_name'], s['ascii_name'], ascii_of(s['name'])):
        sub_alias(t, s['subdivision_code'], 'ISO')
for text, code in [('Neimenggu', 'CN-NM'), ('Inner Mongolia', 'CN-NM'), ('Tibet', 'CN-XZ'), ('Xizang', 'CN-XZ'),
                   ('Guangxi Zhuang', 'CN-GX'), ('Ningxia Hui', 'CN-NX'), ('Xinjiang Uyghur', 'CN-XJ'),
                   ('Shensi', 'CN-SN'), ('Canton', 'CN-GD'), ('Peking', 'CN-BJ'),
                   ('Tokyo-to', 'JP-13'), ('Osaka-fu', 'JP-27'), ('Kyoto-fu', 'JP-26'), ('Hokkaido-do', 'JP-01')]:
    sub_alias(text, code, 'TARIFF_TABLE' if text == 'Neimenggu' else 'MANUAL')
sub_alias_rows = [dict(alias_norm=k[0], **v) for k, v in sorted(sub_aliases.items())]
save('subdivision_aliases.json', {'_comment': 'Spellings of states / provinces / prefectures -> ISO 3166-2 code.',
                                  'aliases': sub_alias_rows})
save('subdivisions.json', {'_comment': 'ISO 3166-2 subdivisions (from pycountry): states, provinces, prefectures.',
                           'subdivisions': subs})

currencies = sorted(({'currency_code': c.alpha_3, 'name': c.name} for c in pycountry.currencies),
                    key=lambda r: r['currency_code'])
save('currencies.json', {'_comment': 'ISO 4217 currencies (from pycountry).', 'currencies': currencies})

units = [
    {'from_unit': 'LB', 'to_unit': 'KG', 'factor': 0.45359237},
    {'from_unit': 'KG', 'to_unit': 'LB', 'factor': 2.2046226218},
    {'from_unit': 'OZ', 'to_unit': 'KG', 'factor': 0.028349523125},
    {'from_unit': 'IN', 'to_unit': 'CM', 'factor': 2.54},
    {'from_unit': 'CM', 'to_unit': 'IN', 'factor': 0.3937007874},
    {'from_unit': 'CUFT', 'to_unit': 'CBM', 'factor': 0.028316846592},
    {'from_unit': 'CBM', 'to_unit': 'CUFT', 'factor': 35.3146667215},
    {'from_unit': 'MI', 'to_unit': 'KM', 'factor': 1.609344},
    {'from_unit': 'KM', 'to_unit': 'MI', 'factor': 0.6213711922},
    {'from_unit': 'CWT_US', 'to_unit': 'KG', 'factor': 45.359237},
]
save('unit_conversions.json', {'_comment': 'Exact conversion factors (value_in_to = value_in_from * factor).', 'units': units})


# ---------------------------------------------------------------- SQL
def q(v):
    if v is None:
        return 'NULL'
    if isinstance(v, bool):
        return 'true' if v else 'false'
    if isinstance(v, (int, float)):
        return repr(v)
    return "'" + str(v).replace("'", "''") + "'"


def insert(table, cols, rows, conflict):
    out = [f'INSERT INTO {table} ({", ".join(cols)}) VALUES']
    out.append(',\n'.join('  (' + ', '.join(q(r.get(c)) for c in cols) + ')' for r in rows))
    out.append(f'{conflict};\n')
    return '\n'.join(out)


def upsert(table, cols, keys, rows):
    sets = ', '.join(f'{c} = EXCLUDED.{c}' for c in cols if c not in keys)
    return insert(table, cols, rows, f'ON CONFLICT ({", ".join(keys)}) DO ' + (f'UPDATE SET {sets}' if sets else 'NOTHING'))


sql = ['-- GENERATED by db/master/tools/build_master.py. Do not edit by hand: change data/*.json and re-run.',
       '-- Run after 001_master_schema.sql. Safe to re-run (upserts).', 'BEGIN;', '']

sql.append(upsert('master.country', ['country_code', 'iso3_code', 'iso_numeric', 'name', 'official_name', 'is_iso'],
                  ['country_code'], countries))
sql.append(upsert('master.country_alias', ['alias_norm', 'alias', 'country_code', 'source'], ['alias_norm'], alias_rows))
cc_rows = [dict(carrier_code=carrier, printed_code=r['carrier_code'], name=r['name'], country_code=r['iso_code'], note=r['note'])
           for carrier, rows in carrier_codes.items() if isinstance(rows, list) for r in rows]
sql.append(upsert('master.carrier_country_code', ['carrier_code', 'printed_code', 'name', 'country_code', 'note'],
                  ['carrier_code', 'printed_code'], cc_rows))
# parents first so the self reference holds
subs_sorted = sorted(subs, key=lambda r: (r['parent_code'] is not None, r['subdivision_code']))
for i in range(0, len(subs_sorted), 1000):
    sql.append(upsert('master.subdivision', ['subdivision_code', 'country_code', 'name', 'short_name', 'ascii_name', 'type', 'parent_code'],
                      ['subdivision_code'], subs_sorted[i:i + 1000]))
for i in range(0, len(sub_alias_rows), 2000):
    sql.append(upsert('master.subdivision_alias', ['alias_norm', 'alias', 'subdivision_code', 'source'],
                      ['alias_norm', 'subdivision_code'], sub_alias_rows[i:i + 2000]))
sql.append(upsert('master.currency', ['currency_code', 'name'], ['currency_code'], currencies))
sql.append(upsert('master.unit_conversion', ['from_unit', 'to_unit', 'factor'], ['from_unit', 'to_unit'], units))

code_rows = []
for list_name, items in load('code_lists.json').items():
    if list_name.startswith('_'):
        continue
    for i, it in enumerate(items):
        code_rows.append({'list_name': list_name, 'code': it['code'], 'label': it['label'],
                          'description': it.get('description') or None, 'sort_order': (i + 1) * 10})
sql.append(upsert('master.code_list', ['list_name', 'code', 'label', 'description', 'sort_order'], ['list_name', 'code'], code_rows))

# B1C04 stations
b4 = load(os.path.join('carriers', 'B1C04_service_areas.json'))
st_rows = [{'carrier_code': 'B1C04', 'country_code': cc, 'station_code': code, 'name': name}
           for cc, stations in b4['stations'].items() for code, name in stations.items()]
sql.append(upsert('master.service_area', ['carrier_code', 'country_code', 'station_code', 'name'],
                  ['carrier_code', 'country_code', 'station_code'], st_rows))

# B1C06: warehouses, district scheme with prefecture members
b6 = load(os.path.join('carriers', 'B1C06.json'))
loc_rows = [dict(carrier_code='B1C06', location_code=l['location_code'], name=l['name'], kind=l['kind'],
                 country_code=l['country_code'], subdivision_code=l['subdivision'], city=l['city'], note=l['note'])
            for l in b6['locations']]
sql.append(upsert('master.carrier_location', ['carrier_code', 'location_code', 'name', 'kind', 'country_code',
                                              'subdivision_code', 'city', 'note'], ['carrier_code', 'location_code'], loc_rows))
zs = b6['zone_scheme']
sql.append(f"""INSERT INTO master.zone_scheme (scheme_code, carrier_code, mode, service_type, direction, home_country_code, zone_basis, rate_card, source_sheet)
VALUES ({q(zs['scheme_code'])}, 'B1C06', 'GROUND', {q(zs['service_type'])}, {q(zs['direction'])}, {q(zs['country_code'])}, {q(zs['zone_basis'])}, '2026 Rates', {q(zs['source'])})
ON CONFLICT (scheme_code) DO UPDATE SET zone_basis = EXCLUDED.zone_basis, source_sheet = EXCLUDED.source_sheet;
""")
sid = f"(SELECT scheme_id FROM master.zone_scheme WHERE scheme_code = {q(zs['scheme_code'])})"
sql.append('INSERT INTO master.zone (scheme_id, zone_code, name) VALUES\n' +
           ',\n'.join(f"  ({sid}, {q(d['zone_code'])}, {q(d['zone_code'])})" for d in b6['districts']) +
           '\nON CONFLICT (scheme_id, zone_code) DO NOTHING;\n')
sql.append(f'DELETE FROM master.zone_member WHERE scheme_id = {sid};')
sql.append('INSERT INTO master.zone_member (scheme_id, zone_code, country_code, subdivision_code, basis, note) VALUES\n' +
           ',\n'.join(f"  ({sid}, {q(d['zone_code'])}, 'JP', {q(m['subdivision'])}, {q(m['basis'])}, {q(m.get('note'))})"
                      for d in b6['districts'] for m in d['members']) + ';\n')

rules = [
    dict(carrier_code='B1C06', mode='GROUND', service_type='GROUPAGE', rule_code='VOLUMETRIC_KG_PER_CBM', value_num=b6['volumetric']['kg_per_cbm'],
         value_text=None, source='tariff.ground_tariff freight rows'),
    dict(carrier_code='B1C05', mode='GROUND', service_type='*', rule_code='WEIGHT_UNIT', value_num=None, value_text='LB',
         source='2026 Rates: every sheet is "Weight (in LB)"; bands are whole pounds'),
    dict(carrier_code='B1C05', mode='AIR', service_type='*', rule_code='WEIGHT_UNIT', value_num=None, value_text='LB',
         source='2026 Rates: every sheet is "Weight (in LB)"; bands are whole pounds'),
    dict(carrier_code='B1C04', mode='GROUND', service_type='*', rule_code='VOLUMETRIC_DIVISOR', value_num=None, value_text='TO CONFIRM',
         source='Each rate card, sheet "S&S Special Agreement", section VOLUMETRIC WEIGHT (value not loaded yet)'),
]
sql.append(upsert('master.carrier_pricing_rule', ['carrier_code', 'mode', 'service_type', 'rule_code', 'value_num', 'value_text', 'source'],
                  ['carrier_code', 'mode', 'service_type', 'rule_code'], rules))
sql.append('COMMIT;')

with open(SEED, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(sql) + '\n')
print(f'countries {len(countries)}, aliases {len(alias_rows)}, subdivisions {len(subs)}, currencies {len(currencies)}, '
      f'code list rows {len(code_rows)}, B1C04 stations {len(st_rows)}, B1C06 members '
      f'{sum(len(d["members"]) for d in b6["districts"])} -> {SEED}')
