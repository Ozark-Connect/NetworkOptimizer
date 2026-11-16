"""
Builds src/NetworkOptimizer.Core/Data/router-places.tsv.gz: the airport and CLLI place codes the
traceroute hop locator reads when its curated list (RouterLocationCities.cs) has no match.

Sources, downloaded fresh on every run:
  - OurAirports airports.csv (public domain): https://ourairports.com/data/
  - GeoNames cities500 and admin1 codes (CC BY 4.0): https://www.geonames.org/
    GeoNames data requires attribution; it is credited in THIRD-PARTY-NOTICES.md.

Rows (tab-separated):
  A  <iata>  <L|M>  <city>  <region>  <country>  <lat>  <lon>      an airport, large or medium
  C  <cccc>  <rr>   <city>  <country> <lat>  <lon>                 a derived CLLI city code in a state or province

CLLI city codes are abbreviations of the city's name (Lincoln LNCL, Little Rock LTRK, Fort Myers FTMY).
The full CLLI list is licensed, so the codes are derived from GeoNames place names instead. A code is
kept only when one town in its state fits, or one is at least 10 times as populous as every other
that fits; the rest are left out rather than guessed.

Usage: python scripts/data/build-router-places.py
"""
import collections
import csv
import gzip
import io
import os
import re
import urllib.request
import zipfile

OUT = os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'NetworkOptimizer.Core', 'Data', 'router-places.tsv.gz')
AIRPORTS_URL = 'https://davidmegginson.github.io/ourairports-data/airports.csv'
CITIES_URL = 'https://download.geonames.org/export/dump/cities500.zip'

# GeoNames writes Canadian provinces as numbers; CLLI uses the postal codes.
CA_PROVINCES = {'01': 'AB', '02': 'BC', '03': 'MB', '04': 'NB', '05': 'NL', '07': 'NS', '08': 'ON',
                '09': 'PE', '10': 'QC', '11': 'SK', '12': 'YT', '13': 'NT', '14': 'NU'}
REGION_COUNTRIES = {'US', 'CA', 'AU'}
# Populated places only: not a section of a city (PPLX), a historical (PPLH), abandoned (PPLQ) or destroyed (PPLW) one.
PLACE_CODES = {'PPL', 'PPLA', 'PPLA2', 'PPLA3', 'PPLA4', 'PPLC', 'PPLG', 'PPLL', 'PPLS'}
DOMINANCE = 10
VOWELS = set('AEIOU')
PREFIX = {'FORT': 'FT', 'SAINT': 'ST', 'MOUNT': 'MT', 'PORT': 'PT', 'POINT': 'PT', 'NORTH': 'N', 'SOUTH': 'S',
          'EAST': 'E', 'WEST': 'W', 'NEW': 'N', 'LAKE': 'LK'}


def fetch(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'router-places-build'})) as r:
        return r.read()


def airports():
    rows = []
    reader = csv.DictReader(io.StringIO(fetch(AIRPORTS_URL).decode('utf-8')))
    for a in reader:
        iata = (a['iata_code'] or '').strip().lower()
        if len(iata) != 3 or not iata.isalpha():
            continue
        if a['type'] == 'large_airport':
            size = 'L'
        elif a['type'] == 'medium_airport' and a['scheduled_service'] == 'yes':
            size = 'M'
        else:
            continue
        city = (a['municipality'] or a['name']).strip()
        country = a['iso_country'].strip().upper()
        region = a['iso_region'].split('-', 1)[1] if country in REGION_COUNTRIES and '-' in a['iso_region'] else ''
        rows.append(('A', iata, size, city, region, country, f"{float(a['latitude_deg']):.3f}", f"{float(a['longitude_deg']):.3f}"))
    return rows


def squeeze(s):
    out = []
    for ch in s:
        if not out or out[-1] != ch:
            out.append(ch)
    return ''.join(out)


def word_forms(w):
    """First letter plus the consonants after it, with and without doubled letters; the earliest
    vowels come back when the consonants run short (HELEN -> HELN, YORK)."""
    forms = set()
    for base in (w, squeeze(w)):
        forms.add(base[0] + ''.join(c for c in base[1:] if c not in VOWELS))
        cons = base[0] + ''.join(c for c in base[1:] if c not in VOWELS)
        if len(cons) < 4:
            keep, need = [], 4 - len(cons)
            for i, c in enumerate(base):
                if i == 0 or c not in VOWELS or need > 0:
                    if i > 0 and c in VOWELS:
                        need -= 1
                    keep.append(c)
            forms.add(''.join(keep))
    return forms


def clli_codes(name):
    words = [w for w in re.split(r'[^A-Z]+', name.upper()) if w]
    out = set()
    if not words:
        return out
    if len(words) == 1:
        for f in word_forms(words[0]):
            if len(f) >= 4:
                out.update({f[:4], f[:2] + f[-2:], f[:3] + f[-1]})
    else:
        a, b = words[0], ''.join(words[1:])
        if a in PREFIX and len(PREFIX[a]) == 1:
            out.update(PREFIX[a] + f[:3] for f in word_forms(b) if len(f) >= 3)
        firsts = {PREFIX[a]} if a in PREFIX and len(PREFIX[a]) == 2 else {f[:2] for f in word_forms(a) if len(f) >= 2}
        for f in word_forms(b):
            if len(f) >= 2:
                for p in firsts:
                    out.update({p + f[:2], p + f[0] + f[-1]})
        initials = ''.join(w[0] for w in words)
        if len(initials) >= 3:
            out.add((initials + words[-1][1:])[:4])
        out.update(f[:4] for f in word_forms(''.join(words)) if len(f) >= 4)
    return {c for c in out if len(c) == 4}


def clli():
    data = zipfile.ZipFile(io.BytesIO(fetch(CITIES_URL))).read('cities500.txt').decode('utf-8')
    fits = collections.defaultdict(dict)    # (code, region) -> {name: (pop, lat, lon, country)}
    for line in data.splitlines():
        p = line.split('\t')
        if p[6] != 'P' or p[7] not in PLACE_CODES or p[8] not in ('US', 'CA'):
            continue
        region = p[10] if p[8] == 'US' else CA_PROVINCES.get(p[10])
        if not region:
            continue
        pop, name = int(p[14] or 0), p[2]
        for code in clli_codes(name):
            seen = fits[(code, region)].get(name)
            # One name twice (two GeoNames rows for one town) is one town: keep the bigger.
            if seen is None or pop > seen[0]:
                fits[(code, region)][name] = (pop, float(p[4]), float(p[5]), p[8])
    rows = []
    for (code, region), towns in fits.items():
        ranked = sorted(towns.items(), key=lambda t: -t[1][0])
        top_name, (top_pop, lat, lon, country) = ranked[0]
        if len(ranked) > 1 and top_pop < DOMINANCE * max(ranked[1][1][0], 1):
            continue
        rows.append(('C', code.lower(), region.lower(), top_name, country, f'{lat:.3f}', f'{lon:.3f}'))
    return rows


def main():
    rows = sorted(airports()) + sorted(clli())
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    body = '\n'.join('\t'.join(r) for r in rows) + '\n'
    # mtime=0 keeps the file byte-identical when the data has not changed.
    with open(OUT, 'wb') as f:
        f.write(gzip.compress(body.encode('utf-8'), mtime=0))
    print(f'{sum(1 for r in rows if r[0] == "A")} airports, {sum(1 for r in rows if r[0] == "C")} CLLI codes -> {os.path.normpath(OUT)}')


if __name__ == '__main__':
    main()
