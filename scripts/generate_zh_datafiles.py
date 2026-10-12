#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Generate Chinese EVEMon datafiles from the Serenity (Chinese) game client localization.

Data sources
------------
The Serenity client ships two Python pickles under
  <EVE>\\SharedCache\\ResFiles\\<xx>\\<hash>
whose paths are declared in SharedCache/serenity/resfileindex.txt:
  localization_fsd_zh.pickle     -> ('zh',    {messageID: (text, None, None), ...})
  localization_fsd_en-us.pickle  -> ('en-us', {messageID: (text, None, None), ...})
Joining both on messageID yields an English -> Chinese name map.

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
import gzip
import html
import os
import pickle
import re
import shutil
import sys
from collections import Counter

# --------------------------------------------------------------------------
# Paths
# --------------------------------------------------------------------------
EVE_CACHE = r'D:\Games\EVE\SharedCache'
SERENITY_INDEX = os.path.join(EVE_CACHE, 'serenity', 'resfileindex.txt')
RESFILES = os.path.join(EVE_CACHE, 'ResFiles')

EVEMON_APPDATA = os.path.join(os.environ.get('APPDATA', ''), 'EVEMon')
# EVEMon source Resources folder (version controlled). The build copies these
# files to the output directory, so generated datafiles survive a clean rebuild.
EVEMON_RESOURCES = r'D:\Works\codes\fanjing\evemon_cn\evemon\src\EVEMon.Common\Resources'

ZIP_LANG = 'zh-CN'
SRC_LANG = 'en-US'

# --------------------------------------------------------------------------
# Datafile translation rules: file -> list of (regex attribute, translator)
# --------------------------------------------------------------------------
# Each rule is a regex whose group(1) is the value to translate.
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


def translate_text(text, en2zh, stats):
    """Translate a full XML attribute/element text, handling HTML entities."""
    original = html.unescape(text)
    if original in en2zh:
        stats['hit'] += 1
        return html.escape(en2zh[original], quote=True)
    stats['miss'] += 1
    stats['miss_samples'][original] += 1
    return text


def process_datafile(name, en2zh, output_dirs, dry_run=False):
    """Translate one EVEMon datafile and write the -zh-CN variant."""
    src = os.path.join(EVEMON_APPDATA, f'{name}-{SRC_LANG}.xml.gzip')
    if not os.path.isfile(src):
        print(f'  !! 源文件不存在，跳过: {src}')
        return None

    with gzip.open(src, 'rt', encoding='utf-8') as f:
        xml = f.read()

    stats = {'hit': 0, 'miss': 0, 'miss_samples': Counter()}
    for pattern in DATAFILE_RULES[name]:
        # re.sub with a function: regex group(1) holds the value to translate.
        # The replacement is spliced in by exact position so that a value which
        # also appears inside the tag name (e.g. name="name") cannot be mangled.
        def repl(m, p=pattern):
            value = m.group(1)
            if not value:
                return m.group(0)
            translated = translate_text(value, en2zh, stats)
            if translated == value:
                return m.group(0)
            start = m.start(1) - m.start(0)
            end = m.end(1) - m.start(0)
            return m.group(0)[:start] + translated + m.group(0)[end:]
        xml = re.sub(pattern, repl, xml)

    total = stats['hit'] + stats['miss']
    rate = stats['hit'] / total * 100 if total else 0
    print(f'  {name}: 命中 {stats["hit"]}/{total} = {rate:.1f}%')
    if stats['miss']:
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

    names = list(DATAFILE_RULES.keys())
    if args.only:
        wanted = {x.strip() for x in args.only.split(',')}
        names = [n for n in names if n in wanted]

    print('生成中文化数据文件:')
    for name in names:
        process_datafile(name, en2zh, output_dirs, args.dry_run)

    write_alias_table(en2zh, output_dirs, args.dry_run)
    print('完成。')


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()
