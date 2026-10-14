#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Append the Serenity-only types to the translated EVEMon items datafile.

Why
---
EVEMon's items datafile comes from the TQ SDE and has no entries for items that only
exist on the Chinese server - about 2000 of them (YC124 cerebral accelerators, DED skill
modules, event SKINs, ...). Everything that resolves an item by ID then silently drops
it. Market orders are the visible symptom: CharacterMarketOrdersList filters on
`Item != null`, so an order whose type is unknown never shows up.

Sources
-------
No network access. The market snapshot supplies typeID, Chinese name, description and the
five market levels; the translated datafile is used both as the thing we extend and as
the teacher, because its group names are Chinese and line up with those market levels.
From it we learn, per market path, the (category, group) pair and the family enum that
EVEMon stores on each item.

Run order
---------
  python generate_zh_datafiles.py     # produces eve-items-zh-CN.xml.gzip
  python inject_serenity_items.py     # appends the Serenity-only entries to it

Usage
-----
  python inject_serenity_items.py [--dry-run]
"""

import argparse
import gzip
import os
import sys
import zipfile
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict

# --------------------------------------------------------------------------
# Paths
# --------------------------------------------------------------------------
EVEDATA_XLSX = r'D:\Works\codes\fanjing\eve_auto_botlab\eve_read_memory_64_bit\data\sde\evedata_20260830.xlsx'
EVEDATA_ITEM_SHEET = 'xl/worksheets/sheet1.xml'

EVEMON_APPDATA = os.path.join(os.environ.get('APPDATA', ''), 'EVEMon')
EVEMON_RESOURCES = r'D:\Works\codes\fanjing\evemon_cn\evemon\src\EVEMon.Common\Resources'
ITEMS_FILENAME = 'eve-items-zh-CN.xml.gzip'

# Shipped market group IDs run from 2 to 11000; generated ones start well clear.
NEW_GROUP_ID_BASE = 900000

# Shipped group that holds everything without a market listing.
NON_MARKET_GROUP_NAME = 'Various Non-Market'


def load_evedata_items():
    """typeID -> (name, description, [market levels])."""
    ns = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
    with zipfile.ZipFile(EVEDATA_XLSX) as z:
        shared = [''.join(t.text or '' for t in si.iter(ns + 't'))
                  for si in ET.fromstring(z.read('xl/sharedStrings.xml'))]
        sheet = ET.fromstring(z.read(EVEDATA_ITEM_SHEET))

    items = {}
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

        if len(values) < 3 or not values[0] or values[0] == 'typeID':
            continue
        items[values[0]] = (values[1], values[2], [v for v in values[3:9] if v])
    return items


def index_tree(root):
    """Collect group elements, their paths, and the items sitting in each group."""
    group_by_path = {}      # path tuple -> element
    path_by_id = {}         # group id -> path tuple
    items_by_group = {}     # group id -> items element

    def walk(node, chain):
        for child in node:
            if child.tag == 'marketGroup':
                path = chain + (child.get('name'),)
                group_by_path[path] = child
                path_by_id[child.get('id')] = path
                walk(child, path)
            elif child.tag == 'marketGroups':
                walk(child, chain)
            elif child.tag == 'items':
                parent = node
                items_by_group.setdefault(parent, child)

    for child in root:
        if child.tag == 'marketGroup':
            path = (child.get('name'),)
            group_by_path[path] = child
            path_by_id[child.get('id')] = path
            walk(child, path)
    return group_by_path, path_by_id, items_by_group


def learn_from_existing(root, group_by_path):
    """Learn market path -> (category, group), and (category, group) -> family."""
    pair_per_path = defaultdict(Counter)
    family_per_pair = defaultdict(Counter)

    def walk(node, chain):
        for child in node:
            if child.tag == 'marketGroup':
                walk(child, chain + (child.get('name'),))
            elif child.tag == 'marketGroups':
                walk(child, chain)
            elif child.tag == 'items':
                path = chain
                for it in child:
                    cat, grp, fam = it.get('category'), it.get('group'), it.get('family')
                    if cat and grp:
                        pair_per_path[path][(cat, grp)] += 1
                        if fam:
                            family_per_pair[(cat, grp)][fam] += 1

    for child in root:
        if child.tag == 'marketGroup':
            walk(child, (child.get('name'),))

    pairs = {p: c.most_common(1)[0][0] for p, c in pair_per_path.items() if c}
    families = {p: c.most_common(1)[0][0] for p, c in family_per_pair.items() if c}
    top_defaults = {}
    top = defaultdict(Counter)
    for path, pair in pairs.items():
        top[path[0]][pair] += 1
    for k, v in top.items():
        if v:
            top_defaults[k] = v.most_common(1)[0][0]
    return pairs, families, top_defaults


def resolve_pair(levels, pairs, top_defaults):
    """Longest prefix match on the market path; falls back to the top level default."""
    for n in range(len(levels), 0, -1):
        key = tuple(levels[:n])
        if key in pairs:
            return pairs[key]
    if levels and levels[0] in top_defaults:
        return top_defaults[levels[0]]
    return ('Commodity', 'Miscellaneous')


def ensure_child(parent, tag, first=False):
    """Get the child with the given tag, creating it if needed."""
    for child in parent:
        if child.tag == tag:
            return child
    element = ET.Element(tag)
    if first:
        parent.insert(0, element)
    else:
        parent.append(element)
    return element


def build_item(tid, name, desc, pair, family):
    element = ET.Element('item')
    element.set('id', tid)
    element.set('name', name)
    element.set('description', desc)
    element.set('category', pair[0])
    element.set('group', pair[1])
    element.set('icon', '')
    element.set('race', 'None')
    element.set('metaLevel', '0')
    element.set('metaGroup', 'T1')
    element.set('slot', 'NoSlot')
    element.set('family', family)
    element.set('portionSize', '1')
    return element


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dry-run', action='store_true', help='只统计，不写文件')
    args = parser.parse_args()

    src = os.path.join(EVEMON_APPDATA, ITEMS_FILENAME)
    if not os.path.isfile(src):
        print(f'源文件不存在: {src}')
        return 1

    with gzip.open(src, 'rt', encoding='utf-8') as f:
        raw = f.read()
    root = ET.fromstring(raw)

    existing_ids = {it.get('id') for it in root.iter('item')}
    ed = load_evedata_items()
    missing = {tid: v for tid, v in ed.items() if tid not in existing_ids}
    print(f'现有物品 {len(existing_ids)}, evedata {len(ed)}, 缺失 {len(missing)}')

    group_by_path, _, _ = index_tree(root)
    pairs, families, top_defaults = learn_from_existing(root, group_by_path)
    print(f'现有组 {len(group_by_path)}, 学到路径映射 {len(pairs)}')

    # 非市场物品的落点
    non_market = group_by_path.get((NON_MARKET_GROUP_NAME,))
    if non_market is None:
        print(f'!! 找不到「{NON_MARKET_GROUP_NAME}」组，非市场物品无法归位')
        return 1

    placed = Counter()
    new_groups = {}
    next_id = NEW_GROUP_ID_BASE

    def group_for(levels):
        """Walk the market levels, creating groups that do not exist yet."""
        nonlocal next_id
        parent = root
        path = ()
        for name in levels:
            path = path + (name,)
            element = group_by_path.get(path)
            if element is None:
                element = ET.Element('marketGroup')
                element.set('id', str(next_id))
                element.set('name', name)
                next_id += 1
                container = ensure_child(parent, 'marketGroups') if parent is not root \
                    else root
                container.append(element)
                group_by_path[path] = element
                new_groups[' > '.join(path)] = element.get('id')
            parent = element
        return parent

    for tid, (name, desc, levels) in missing.items():
        if levels:
            pair = resolve_pair(levels, pairs, top_defaults)
            target = group_for(levels)
            placed['按分类归位'] += 1
        else:
            pair = resolve_pair([], pairs, top_defaults)
            target = non_market
            placed['归入非市场组'] += 1

        family = families.get(pair, 'Item')
        ensure_child(target, 'items', first=True).append(
            build_item(tid, name, desc, pair, family))

    print()
    for k, v in placed.most_common():
        print(f'  {k}: {v}')
    print(f'  新建组: {len(new_groups)}')
    for k in list(new_groups)[:8]:
        print(f'    {k} (id={new_groups[k]})')

    if args.dry_run:
        print('(dry-run，未写文件)')
        return 0

    payload = ET.tostring(root, encoding='unicode')
    payload = '<?xml version="1.0" encoding="utf-8"?>\n' + payload

    for d in (EVEMON_RESOURCES, EVEMON_APPDATA):
        if not os.path.isdir(d):
            continue
        out = os.path.join(d, ITEMS_FILENAME)
        with gzip.open(out, 'wt', encoding='utf-8') as f:
            f.write(payload)
        print(f'已写出: {out} ({os.path.getsize(out)} bytes)')
    return 0


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    sys.exit(main())
