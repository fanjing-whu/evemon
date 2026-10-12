#!/usr/bin/env python3
"""
Generate EVEMon-compatible Chinese skill data file from Pyfa FSD data.

Output: eve-skills-pyfa-zh-CN.xml.gzip
  - Skill names in Chinese (typeName_zh)
  - Skill group names in Chinese (groupName_zh)
  - Falls back to English when Chinese is not available
"""

import json
import os
import glob
import gzip
import sys
from xml.etree.ElementTree import Element, SubElement, tostring
from xml.dom import minidom

sys.stdout.reconfigure(encoding='utf-8')

# Paths
PYFA_DIR = r'D:\Works\codes\fanjing\evemon_cn\Pyfa\staticdata\fsd_built'
OUTPUT_DIR = r'D:\Works\codes\fanjing\evemon_cn\evemon\src\EVEMon\bin\Release\net8.0-windows\Resources'
OUTPUT_FILE_ZH = os.path.join(OUTPUT_DIR, 'eve-skills-pyfa-zh-CN.xml.gzip')
OUTPUT_FILE_EN = os.path.join(OUTPUT_DIR, 'eve-skills-en-US.xml.gzip')

# Attribute ID mapping.
# Verified against Pyfa FSD dogmaattributes.0.json:
#   164 = charisma, 165 = intelligence, 166 = memory, 167 = perception, 168 = willpower
# (The three first entries were previously shifted by one, which swapped
#  intelligence/memory/charisma on every generated skill.)
ATTR_NAMES = {
    164: 'charisma',
    165: 'intelligence',
    166: 'memory',
    167: 'perception',
    168: 'willpower'
}

def load_pyfa_data():
    """Load all necessary Pyfa FSD data files."""
    print("Loading Pyfa FSD data...")
    
    all_types = {}
    for types_file in sorted(glob.glob(os.path.join(PYFA_DIR, 'types.*.json'))):
        with open(types_file, encoding='utf-8') as f:
            data = json.load(f)
        all_types.update(data)
    print(f"  Loaded {len(all_types)} types")
    
    with open(os.path.join(PYFA_DIR, 'groups.0.json'), encoding='utf-8') as f:
        groups_data = json.load(f)
    print(f"  Loaded {len(groups_data)} groups")
    
    with open(os.path.join(PYFA_DIR, 'requiredskillsfortypes.0.json'), encoding='utf-8') as f:
        req_data = json.load(f)
    print(f"  Loaded {len(req_data)} required skills entries")
    
    typedogma_data = {}
    for i in range(3):
        fpath = os.path.join(PYFA_DIR, f'typedogma.{i}.json')
        if os.path.exists(fpath):
            with open(fpath, encoding='utf-8') as f:
                data = json.load(f)
                typedogma_data.update(data)
    print(f"  Loaded {len(typedogma_data)} typedogma entries")
    
    return all_types, groups_data, req_data, typedogma_data


def get_skill_attributes(tid, typedogma_data):
    """Extract skill-specific attributes from typedogma data."""
    tdogma = typedogma_data.get(str(tid), {}).get('dogmaAttributes', [])
    return {a['attributeID']: a['value'] for a in tdogma}


def build_skill_groups(all_types, groups_data, typedogma_data):
    """Build skill groups organized by group ID."""
    skill_ids = set()
    for tid, tdata in typedogma_data.items():
        attrs = tdata.get('dogmaAttributes', [])
        attr_ids = [a.get('attributeID') for a in attrs]
        if 180 in attr_ids:  # primaryAttribute means it's a skill
            skill_ids.add(int(tid))
    
    print(f"Found {len(skill_ids)} skills with primaryAttribute")
    
    skill_groups = {}
    for sid in skill_ids:
        tdata = all_types.get(str(sid), {})
        gid = tdata.get('groupID', 0)
        if gid not in skill_groups:
            skill_groups[gid] = []
        skill_groups[gid].append(sid)
    
    group_info = {}
    for gid in skill_groups:
        gdata = groups_data.get(str(gid), {})
        name_en = gdata.get('groupName_en-us', f'Group {gid}')
        name_zh = gdata.get('groupName_zh') or name_en
        group_info[gid] = {
            'name_en': name_en,
            'name_zh': name_zh,
            'skills': sorted(skill_groups[gid])
        }
    
    return group_info


def attr_id_to_name(attr_id):
    return ATTR_NAMES.get(int(attr_id), f'attr{attr_id}')


def generate_xml(all_types, groups_data, req_data, typedogma_data, skill_groups, use_chinese=True):
    """Generate EVEMon format XML."""
    lang = "Chinese" if use_chinese else "English"
    print(f"Generating {lang} XML...")
    
    root = Element('skills')
    
    stats = {'zh_names': 0, 'en_names': 0, 'no_names': 0}
    
    for gid in sorted(skill_groups.keys()):
        group_elem = SubElement(root, 'group')
        group_elem.set('id', str(gid))
        
        if use_chinese:
            group_elem.set('name', skill_groups[gid]['name_zh'])
        else:
            group_elem.set('name', skill_groups[gid]['name_en'])
        
        for sid in skill_groups[gid]['skills']:
            tdata = all_types.get(str(sid), {})
            
            name_en = tdata.get('typeName_en-us', f'Skill {sid}')
            name_zh = tdata.get('typeName_zh')
            description_en = tdata.get('description_en-us', '')
            description_zh = tdata.get('description_zh', '')
            published = tdata.get('published', True)
            
            if use_chinese:
                if name_zh:
                    display_name = name_zh
                    stats['zh_names'] += 1
                elif name_en:
                    display_name = name_en
                    stats['en_names'] += 1
                else:
                    display_name = f'Skill {sid}'
                    stats['no_names'] += 1
                description = description_zh or description_en or ''
            else:
                display_name = name_en or f'Skill {sid}'
                description = description_en or ''
            
            attrs = get_skill_attributes(sid, typedogma_data)
            primary = attr_id_to_name(attrs.get(180, 164))
            secondary = attr_id_to_name(attrs.get(181, 165))
            rank = int(attrs.get(275, 1))
            cost = int(rank * 256000)
            
            skill_elem = SubElement(group_elem, 'skill')
            skill_elem.set('public', 'true' if published else 'false')
            skill_elem.set('id', str(sid))
            skill_elem.set('name', display_name)
            if description:
                skill_elem.set('description', description[:500])  # Limit length
            skill_elem.set('primaryAttr', primary)
            skill_elem.set('secondaryAttr', secondary)
            skill_elem.set('rank', str(rank))
            skill_elem.set('cost', str(cost))
            
            # Add prerequisites
            prereqs = req_data.get(str(sid), {})
            for req_id, req_level in prereqs.items():
                prereq_elem = SubElement(skill_elem, 'prereq')
                req_tdata = all_types.get(str(req_id), {})
                if use_chinese:
                    req_name = req_tdata.get('typeName_zh') or req_tdata.get('typeName_en-us', f'Skill {req_id}')
                else:
                    req_name = req_tdata.get('typeName_en-us', f'Skill {req_id}')
                prereq_elem.set('id', str(req_id))
                prereq_elem.set('name', req_name)
                prereq_elem.set('level', str(req_level))
            
            alpha_elem = SubElement(skill_elem, 'AlphaLimit')
            alpha_elem.text = '0'
    
    if use_chinese:
        print(f"  Name stats: {stats['zh_names']} Chinese, {stats['en_names']} English fallback, {stats['no_names']} generated")
    
    return root


def prettify_xml(elem):
    rough_string = tostring(elem, encoding='unicode')
    reparsed = minidom.parseString(rough_string)
    return reparsed.toprettyxml(indent="  ")


def write_gzip_xml(root, output_path):
    xml_string = prettify_xml(root)
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with gzip.open(output_path, 'wt', encoding='utf-8') as f:
        f.write(xml_string)
    size = os.path.getsize(output_path)
    print(f"  Written: {output_path} ({size // 1024}KB)")


def main():
    print("=" * 60)
    print("Pyfa to EVEMon Skill Data Converter (Bilingual)")
    print("=" * 60)
    
    all_types, groups_data, req_data, typedogma_data = load_pyfa_data()
    skill_groups = build_skill_groups(all_types, groups_data, typedogma_data)
    
    print(f"\nBuilt {len(skill_groups)} skill groups")
    total_skills = sum(len(v['skills']) for v in skill_groups.values())
    print(f"Total skills: {total_skills}")
    
    print()
    
    # Generate Chinese version
    print("--- Generating Chinese version (eve-skills-pyfa-zh-CN.xml.gzip) ---")
    root_zh = generate_xml(all_types, groups_data, req_data, typedogma_data, skill_groups, use_chinese=True)
    write_gzip_xml(root_zh, OUTPUT_FILE_ZH)
    
    print()
    
    # Generate English version (also update)
    print("--- Generating English version (eve-skills-en-US.xml.gzip) ---")
    root_en = generate_xml(all_types, groups_data, req_data, typedogma_data, skill_groups, use_chinese=False)
    write_gzip_xml(root_en, OUTPUT_FILE_EN)
    
    print()
    print("Done!")
    print(f"  Chinese: {OUTPUT_FILE_ZH}")
    print(f"  English: {OUTPUT_FILE_EN}")


if __name__ == '__main__':
    main()
