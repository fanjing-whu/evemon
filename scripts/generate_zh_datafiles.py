#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Generate Chinese EVEMon datafiles from the Serenity (Chinese) game client
localization and the TQ SDE translation table.

Data sources
------------
1. Serenity client pickles, paths declared in
   SharedCache/serenity/resfileindex.txt:
     localization_fsd_zh.pickle     -> ('zh',    {messageID: (text, None, None), ...})
     localization_fsd_en-us.pickle  -> ('en-us', {messageID: (text, None, None), ...})
   Joining both on messageID yields an English -> Chinese map. It covers names
   (items, systems, agents, ...) but descriptions only partially, because the
   EVEMon datafiles store the description as one long paragraph while the pickle
   holds it as separate message fragments.

2. TQ SDE translation table (trnTranslations.csv), keyed by
   (tcID, keyID, languageID):
     tcID 8  -> object name,        keyID = typeID
     tcID 33 -> object description, keyID = typeID
   Indexing by typeID gives full description coverage. The text carries EVE
   markup (<br>, <a href=showinfo:..>, <font ..>, <url=..>) which is stripped
   because the English datafiles store plain single-line text.

Output (written to the EVEMon Resources folder and %APPDATA%\\EVEMon)
--------------------------------------------------------------------
  eve-<type>-zh-CN.xml.gzip   - datafiles with names/descriptions translated
  eve-name-alias-zh.txt.gzip  - "English<TAB>Chinese" alias table, used by
                                EVEMon at runtime to resolve English names
                                coming from ESI / EFT / XML sources

Usage
-----
  python generate_zh_datafiles.py [--dry-run] [--only items,skills,...]
"""

import argparse
import csv
import gzip
import html
import os
import pickle
import re
import shutil
import sys
import zipfile
import xml.etree.ElementTree as ET
from collections import Counter

# --------------------------------------------------------------------------
# Paths
# --------------------------------------------------------------------------
EVE_CACHE = r'D:\Games\EVE\SharedCache'
SERENITY_INDEX = os.path.join(EVE_CACHE, 'serenity', 'resfileindex.txt')
RESFILES = os.path.join(EVE_CACHE, 'ResFiles')

# TQ SDE translation table
TRN_TRANSLATIONS = r'C:\Users\Fanjing\AppData\Local\EveAutomation\sde\trnTranslations.csv'

# Serenity market snapshot workbook (ceve-market export). Its "NPC空间站" sheet maps
# stationID -> Chinese station name, which is the only way to translate station names:
# they are assembled at runtime by the client and have no entry in the localization
# pickles, and the two formats differ (EVEMon "Jita IV - Moon 4 - ..." vs the client's
# "吉他IV-卫星4-...").
EVEDATA_XLSX = r'D:\Works\codes\fanjing\eve_auto_botlab\eve_read_memory_64_bit\data\sde\evedata_20260830.xlsx'
EVEDATA_STATION_SHEET = 'xl/worksheets/sheet5.xml'
EVEDATA_ITEM_SHEET = 'xl/worksheets/sheet1.xml'

EVEMON_APPDATA = os.path.join(os.environ.get('APPDATA', ''), 'EVEMon')
# EVEMon source Resources folder (version controlled). The build copies these
# files to the output directory, so generated datafiles survive a clean rebuild.
EVEMON_RESOURCES = r'D:\Works\codes\fanjing\evemon_cn\evemon\src\EVEMon.Common\Resources'

ZIP_LANG = 'zh-CN'
SRC_LANG = 'en-US'

# --------------------------------------------------------------------------
# Datafile translation rules: file -> list of regex whose group(1) is the value
# --------------------------------------------------------------------------
DATAFILE_RULES = {
    'eve-items': [r'name="([^"]*)"', r'description="([^"]*)"'],
    'eve-skills': [r'name="([^"]*)"', r'description="([^"]*)"'],
    'eve-geography': [r'name="([^"]*)"', r'corporationName="([^"]*)"', r'agentName="([^"]*)"'],
    'eve-blueprints': [r'name="([^"]*)"'],
    'eve-certificates': [r'name="([^"]*)"', r'description="([^"]*)"'],
    'eve-masteries': [r'name="([^"]*)"', r'className="([^"]*)"'],
    'eve-properties': [r'<name>([^<]*)</name>', r'<description>([^<]*)</description>'],
    # eve-reprocessing only contains IDs - nothing to translate
}

# Datafiles whose elements carry a typeID we can look up in the translation table
TYPEID_DATAFILES = {'eve-items': 'item', 'eve-skills': 'skill'}

# --------------------------------------------------------------------------
# EVE markup handling
# --------------------------------------------------------------------------
_BLOCK_TAG = re.compile(r'<\s*(?:br|p|div)\b[^>]*>', re.IGNORECASE)
_ANY_TAG = re.compile(r'<[^>]{0,200}>')
_CJK = re.compile(r'[\u4e00-\u9fff]')


def clean_markup(text):
    """Strip EVE markup, keeping the inner text, and collapse whitespace.

    The English datafiles store descriptions as one plain paragraph, so block
    level markup becomes a space and everything else is dropped.
    """
    if not text:
        return text
    out = _BLOCK_TAG.sub(' ', text)
    out = _ANY_TAG.sub('', out)
    out = html.unescape(out)
    out = re.sub(r'[ \t\r\n]+', ' ', out)
    return out.strip()


def get_attr_value(tag, attr):
    """Return the unescaped value of an attribute inside a tag string."""
    m = re.search(attr + r'="([^"]*)"', tag)
    return html.unescape(m.group(1)) if m else None


def set_attr_value(tag, attr, value):
    """Replace the value of an attribute inside a tag string."""
    m = re.search(attr + r'="([^"]*)"', tag)
    if not m:
        return tag
    return tag[:m.start(1)] + html.escape(value, quote=True) + tag[m.end(1):]


# --------------------------------------------------------------------------
# Source loading
# --------------------------------------------------------------------------
def resolve_pickle_path(filename):
    """Look up a resource path in the Serenity resfileindex and return the file path."""
    with open(SERENITY_INDEX, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if f'localizationfsd/{filename}' not in line:
                continue
            parts = line.split(',')
            rel = parts[1]
            path = os.path.join(RESFILES, rel.replace('/', os.sep))
            if os.path.isfile(path):
                return path
            raise FileNotFoundError(f'resfileindex points at missing file: {path}')
    raise FileNotFoundError(f'{filename} not found in {SERENITY_INDEX}')


def load_pickles():
    """Load the zh and en-us localization pickles and return the English->Chinese map."""
    zh_path = resolve_pickle_path('localization_fsd_zh.pickle')
    en_path = resolve_pickle_path('localization_fsd_en-us.pickle')

    print(f'加载中文本地化: {os.path.basename(zh_path)}')
    with open(zh_path, 'rb') as f:
        _, zh = pickle.load(f, encoding='latin1')
    print(f'  条目数: {len(zh)}')
    print(f'加载英文本地化: {os.path.basename(en_path)}')
    with open(en_path, 'rb') as f:
        _, en = pickle.load(f, encoding='latin1')
    print(f'  条目数: {len(en)}')

    en2zh = {}
    for mid, val in en.items():
        other = zh.get(mid)
        if not val or not other:
            continue
        e, z = val[0], other[0]
        # Keep entries where both sides exist, even when they are identical:
        # many wormhole systems and some constellations share the English and
        # Chinese spelling, and dropping them would understate the hit rate
        # (they simply translate to themselves).
        if e and z:
            en2zh.setdefault(e, z)
    return en2zh


def load_type_translations():
    """Load trnTranslations.csv and return (names, descriptions) keyed by typeID."""
    if not os.path.isfile(TRN_TRANSLATIONS):
        print(f'  !! 翻译表不存在: {TRN_TRANSLATIONS}')
        return {}, {}

    csv.field_size_limit(10 ** 9)
    names, descriptions = {}, {}
    with open(TRN_TRANSLATIONS, encoding='utf-8-sig', newline='') as f:
        reader = csv.reader(f)
        header = next(reader)
        idx = {name: i for i, name in enumerate(header)}
        tc, key = idx['tcID'], idx['keyID']
        lang, text = idx['languageID'], idx['text']
        for row in reader:
            if row[lang] != 'zh':
                continue
            if row[tc] == '8':
                names[row[key]] = row[text]
            elif row[tc] == '33':
                descriptions[row[key]] = row[text]

    print(f'翻译表: 名称 {len(names)} 条, 描述 {len(descriptions)} 条')
    return names, descriptions


def load_station_names():
    """Load stationID -> Chinese station name from the Serenity market workbook.

    Read with the standard library only (xlsx is a zip of XML), so the script has no
    third-party dependency.
    """
    return _load_evedata_sheet(EVEDATA_STATION_SHEET, 2, '站名表（NPC空间站）')


def load_evedata_items():
    """Load typeID -> (name, description) from the Serenity market workbook.

    Used as a last-resort layer: anything the localization pickle and the TQ translation
    table could not resolve, mostly Serenity-only items.
    """
    return _load_evedata_sheet(EVEDATA_ITEM_SHEET, 3, '物品表（evedata）')


def _load_evedata_sheet(sheet_path, min_cols, label):
    if not os.path.isfile(EVEDATA_XLSX):
        print(f'  !! evedata 工作簿不存在: {EVEDATA_XLSX}')
        return {}

    ns = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
    with zipfile.ZipFile(EVEDATA_XLSX) as z:
        shared = [''.join(t.text or '' for t in si.iter(ns + 't'))
                  for si in ET.fromstring(z.read('xl/sharedStrings.xml'))]
        sheet = ET.fromstring(z.read(sheet_path))

    out = {}
    for row in sheet.findall(f'{ns}sheetData/{ns}row'):
        values = []
        for cell in row.findall(f'{ns}c'):
            node = cell.find(f'{ns}v')
            if node is None or node.text is None:
                values.append('')
            elif cell.get('t') == 's':
                values.append(shared[int(node.text)])
            else:
                values.append(node.text)
        if len(values) < min_cols or not values[0]:
            continue
        if values[0] in ('空间站ID', 'typeID'):
            continue
        out[values[0]] = tuple(values[1:min_cols]) if min_cols > 2 else values[1]

    print(f'{label}: {len(out)} 条')
    return out


def apply_evedata_fallback(xml, element, evedata_items, stats):
    """Fill in names and descriptions still in English from the Serenity snapshot.

    Runs last, so it only touches entries the pickle alias map and the TQ translation
    table could not resolve.
    """
    pattern = re.compile(r'<' + element + r'\b[^>]*>')

    def repl(m):
        tag = m.group(0)
        type_id = get_attr_value(tag, 'id')
        entry = evedata_items.get(type_id) if type_id else None
        if not entry:
            return tag

        name, desc = entry
        current = get_attr_value(tag, 'name')
        if current and not _CJK.search(current) and name:
            tag = set_attr_value(tag, 'name', name)
            stats['name_evedata'] += 1

        current = get_attr_value(tag, 'description')
        if current and not _CJK.search(current) and desc:
            cleaned = clean_markup(desc)
            if cleaned:
                tag = set_attr_value(tag, 'description', cleaned)
                stats['desc_evedata'] += 1

        return tag

    return pattern.sub(repl, xml)


def apply_station_names(xml, station_names, stats):
    """Replace station names with their Chinese counterparts, looked up by station ID."""
    pattern = re.compile(r'<stations\b[^>]*>')

    def repl(m):
        tag = m.group(0)
        station_id = get_attr_value(tag, 'id')
        if not station_id:
            return tag
        name = station_names.get(station_id)
        if not name:
            stats['station_miss'] += 1
            return tag
        stats['station_hit'] += 1
        return set_attr_value(tag, 'name', name)

    return pattern.sub(repl, xml)


# --------------------------------------------------------------------------
# Translation
# --------------------------------------------------------------------------
def new_stats():
    return {
        'name_hit': 0, 'name_miss': 0,
        'desc_hit': 0, 'desc_miss': 0,
        'station_hit': 0, 'station_miss': 0,
        'name_evedata': 0, 'desc_evedata': 0,
        'miss_samples': Counter(),
    }


def translate_value(value, en2zh, stats, is_desc):
    """Translate an XML attribute/element text, handling HTML entities."""
    original = html.unescape(value)
    if original in en2zh:
        stats['desc_hit' if is_desc else 'name_hit'] += 1
        return html.escape(en2zh[original], quote=True)

    # Values that are already Chinese are translations, not misses
    if not _CJK.search(original):
        stats['desc_miss' if is_desc else 'name_miss'] += 1
        stats['miss_samples'][original[:60]] += 1
    return value


def apply_type_descriptions(xml, element, type_descs, stats):
    """Overwrite descriptions with the translation table entry for the element typeID."""
    pattern = re.compile(r'<' + element + r'\b[^>]*>')

    def repl(m):
        tag = m.group(0)
        type_id = get_attr_value(tag, 'id')
        if not type_id or type_id not in type_descs:
            return tag
        new_desc = clean_markup(type_descs[type_id])
        if not new_desc:
            return tag
        stats['desc_hit'] += 1
        return set_attr_value(tag, 'description', new_desc)

    return pattern.sub(repl, xml)


def translate_datafile_xml(name, xml, en2zh, type_names, type_descs, station_names,
                           evedata_items, stats):
    """Apply the translation table (description) and the alias map (names)."""
    element = TYPEID_DATAFILES.get(name)
    if element:
        xml = apply_type_descriptions(xml, element, type_descs, stats)

    if name == 'eve-geography' and station_names:
        xml = apply_station_names(xml, station_names, stats)

    for pattern in DATAFILE_RULES[name]:
        is_desc = 'description' in pattern

        # re.sub with a function: regex group(1) holds the value to translate.
        # The replacement is spliced in by exact position so that a value which
        # also appears inside the tag name (e.g. name="name") cannot be mangled.
        def repl(m, p=pattern, d=is_desc):
            value = m.group(1)
            if not value:
                return m.group(0)
            translated = translate_value(value, en2zh, stats, is_desc=d)
            if translated == value:
                return m.group(0)
            start = m.start(1) - m.start(0)
            end = m.end(1) - m.start(0)
            return m.group(0)[:start] + translated + m.group(0)[end:]

        xml = re.sub(pattern, repl, xml)

    # Last resort: names/descriptions still in English from the Serenity snapshot
    if element and evedata_items:
        xml = apply_evedata_fallback(xml, element, evedata_items, stats)

    return xml


def process_datafile(name, en2zh, type_names, type_descs, station_names, evedata_items,
                     output_dirs, dry_run=False):
    """Translate one EVEMon datafile and write the -zh-CN variant."""
    src = os.path.join(EVEMON_APPDATA, f'{name}-{SRC_LANG}.xml.gzip')
    if not os.path.isfile(src):
        print(f'  !! 源文件不存在，跳过: {src}')
        return None

    with gzip.open(src, 'rt', encoding='utf-8') as f:
        xml = f.read()

    stats = new_stats()
    xml = translate_datafile_xml(name, xml, en2zh, type_names, type_descs, station_names,
                                 evedata_items, stats)

    name_total = stats['name_hit'] + stats['name_miss']
    desc_total = stats['desc_hit'] + stats['desc_miss']
    name_rate = stats['name_hit'] / name_total * 100 if name_total else 0
    desc_rate = stats['desc_hit'] / desc_total * 100 if desc_total else 0
    print(f'  {name}: 名称 {stats["name_hit"]}/{name_total} = {name_rate:.1f}%, '
          f'描述 {stats["desc_hit"]}/{desc_total} = {desc_rate:.1f}%')
    if stats['name_evedata'] or stats['desc_evedata']:
        print(f'    evedata 兜底补充: 名称 {stats["name_evedata"]} 项, '
              f'描述 {stats["desc_evedata"]} 项')
    station_total = stats['station_hit'] + stats['station_miss']
    if station_total:
        station_rate = stats['station_hit'] / station_total * 100
        print(f'    空间站名 {stats["station_hit"]}/{station_total} = {station_rate:.1f}%')
    if stats['miss_samples']:
        top = stats['miss_samples'].most_common(3)
        print(f'    未命中样例: {[t[0][:40] for t in top]}')

    if dry_run:
        return stats

    out_name = f'{name}-{ZIP_LANG}.xml.gzip'
    for d in output_dirs:
        if not os.path.isdir(d):
            continue
        out_path = os.path.join(d, out_name)
        with gzip.open(out_path, 'wt', encoding='utf-8') as f:
            f.write(xml)
        print(f'    已写出: {out_path} ({os.path.getsize(out_path)} bytes)')
    return stats


def write_alias_table(en2zh, output_dirs, dry_run=False):
    """Write the English->Chinese alias table used at runtime."""
    # Only keep entries that look like names of game objects: no newlines,
    # reasonable length, and containing at least one ASCII letter.
    lines = []
    for e, z in sorted(en2zh.items()):
        if not e or not z or len(e) > 120 or '\n' in e or '\r' in e:
            continue
        if not any(c.isalpha() and ord(c) < 128 for c in e):
            continue
        # An alias that maps a name to itself is useless and only bloats the table
        if e == z:
            continue
        lines.append(f'{e}\t{z}')
    payload = '\n'.join(lines)
    print(f'别名表: {len(lines)} 条')
    if dry_run:
        return len(lines)
    for d in output_dirs:
        if not os.path.isdir(d):
            continue
        out_path = os.path.join(d, 'eve-name-alias-zh.txt.gzip')
        with gzip.open(out_path, 'wt', encoding='utf-8') as f:
            f.write(payload)
        print(f'  已写出: {out_path} ({os.path.getsize(out_path)} bytes)')
    return len(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dry-run', action='store_true', help='只统计命中率，不写文件')
    parser.add_argument('--only', default='', help='只处理指定数据文件（逗号分隔）')
    args = parser.parse_args()

    output_dirs = [d for d in (EVEMON_RESOURCES, EVEMON_APPDATA) if os.path.isdir(d)]
    print(f'输出目录: {output_dirs}')

    en2zh = load_pickles()
    print(f'英中映射: {len(en2zh)} 条')

    type_names, type_descs = load_type_translations()
    station_names = load_station_names()
    evedata_items = load_evedata_items()

    names = list(DATAFILE_RULES.keys())
    if args.only:
        wanted = {x.strip() for x in args.only.split(',')}
        names = [n for n in names if n in wanted]

    print('生成中文化数据文件:')
    for name in names:
        process_datafile(name, en2zh, type_names, type_descs, station_names, evedata_items,
                         output_dirs, args.dry_run)

    write_alias_table(en2zh, output_dirs, args.dry_run)
    print('完成。')


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()
