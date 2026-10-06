"""Extract geography from the B1C04 rate cards into db/master/data.

Writes
  data/rate_card_country_spellings.json  every 'Country (CC)' spelling found on zone sheets -> country_alias seed
  data/carriers/B1C04_service_areas.json  station codes per country, and per rate card the *1/*2 service-area groups

Only country names, station codes and sheet names are written: no carrier or shipper names.
Usage: python extract_rate_card_geography.py [folder with the B1C04 .xlsx files]
"""
import collections
import glob
import json
import os
import re
import sys

import openpyxl

SRC = sys.argv[1] if len(sys.argv) > 1 else r'E:\EM6 Program\B1C04'
DATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'data')

NAME_CC = re.compile(r'^\s*(.+?)\s*\(([A-Z]{2})\)\s*(\*\d)?\s*$')
# one rate card writes 'United Kingdom *1' without the code
NAME_STAR = re.compile(r'^\s*(United Kingdom|France|Germany|Italy)\s*(\*\d)\s*$', re.I)
BY_NAME = {'united kingdom': 'GB', 'france': 'FR', 'germany': 'DE', 'italy': 'IT'}
STATION = re.compile(r'([^,()]+?)\s*\(([A-Z0-9]{3})\)')
# rate card id from the file name, e.g. EMEA_V01_BE, AP_LCY_V02_CN (the shipper name in between is dropped)
CARD = re.compile(r'ID_\d+_\d+_(.+?_V\d+)_.*_([A-Z]{2})_\d{8}')


def clean(s):
    return re.sub(r'\s+', ' ', s.replace('\n', ' ').replace('\ufffd', '').replace('\xa0', ' ')).strip()


spellings = collections.defaultdict(set)
stations = collections.defaultdict(dict)
groups = []

for path in sorted(glob.glob(os.path.join(SRC, '*.xlsx'))):
    m = CARD.search(os.path.basename(path))
    card = f'{m.group(1)}_{m.group(2)}' if m else 'SG_DOM_2026'
    wb = openpyxl.load_workbook(path, read_only=True, data_only=True)
    for ws in wb.worksheets:
        if 'Zones' not in ws.title:
            continue
        for row in ws.iter_rows(values_only=True):
            for i, v in enumerate(row):
                if not isinstance(v, str):
                    continue
                text = clean(v)
                nm = NAME_CC.match(text)
                if nm:
                    name, cc, star = nm.groups()
                    spellings[cc].add(name)
                else:
                    ns = NAME_STAR.match(text)
                    if not ns:
                        continue
                    name, star = ns.groups()
                    cc = BY_NAME[name.lower()]
                if not star:
                    continue
                nxt = next((x for x in row[i + 1:] if x not in (None, '')), None)
                # '*1 | 3' is the zone of the group; '*1 | Rest of ...' or '*1 | City (ABC), ...' defines it
                if not isinstance(nxt, str) or re.fullmatch(r'[A-Z0-9]{1,2}', nxt.strip()):
                    continue
                desc = clean(nxt)
                rest = desc.lower().startswith('rest of')
                found = STATION.findall(desc)
                for st_name, code in found:
                    stations[cc][code] = clean(st_name)
                groups.append({'rate_card': card, 'sheet': ws.title, 'country': cc, 'group': star,
                               'rest_of_country': rest, 'stations': [] if rest else [c for _, c in found]})

# one definition per (rate card, sheet, country, group)
uniq = {(g['rate_card'], g['sheet'], g['country'], g['group']): g for g in groups}
os.makedirs(os.path.join(DATA, 'carriers'), exist_ok=True)
with open(os.path.join(DATA, 'rate_card_country_spellings.json'), 'w', encoding='utf-8') as f:
    json.dump({'_comment': 'Country spellings printed on B1C04 zone sheets, by ISO-like code. Feeds master.country_alias.',
               'spellings': {k: sorted(v) for k, v in sorted(spellings.items())}}, f, ensure_ascii=False, indent=1)
with open(os.path.join(DATA, 'carriers', 'B1C04_service_areas.json'), 'w', encoding='utf-8') as f:
    json.dump({'_comment': 'B1C04 prices some countries by service area (carrier station). On each rate card a country can be split '
                           'into groups *1, *2, ...; a group is either a list of stations or "rest of country". The meaning of *1 '
                           'differs between rate cards. Postcode -> station is NOT in the files and must come from the carrier.',
               'carrier_code': 'B1C04',
               'stations': {k: dict(sorted(v.items())) for k, v in sorted(stations.items())},
               'groups': sorted(uniq.values(), key=lambda g: (g['rate_card'], g['sheet'], g['country'], g['group']))},
              f, ensure_ascii=False, indent=1)
print('countries', len(spellings), '| stations', {k: len(v) for k, v in stations.items()},
      '| group definitions', len(uniq), '| rate cards', len({g['rate_card'] for g in uniq.values()}))
