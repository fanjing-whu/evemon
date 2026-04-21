# Pyfa to EVEMon Skill Data Converter

This script converts skill data from Pyfa's FSD (Fan-made Static Data) format to EVEMon's XML gzip format.

## Purpose

When ESI (EVE Swagger Interface) is unavailable (e.g., Seren server authentication issues), EVEMon can use a local static skill database as fallback. This script generates that database from Pyfa's publicly available FSD files.

## Data Sources

Pyfa's FSD data is located at: `D:\Works\codes\fanjing\evemon_cn\Pyfa\staticdata\fsd_built`

Key files used:
- `types.*.json` - Type definitions (51,672 items)
- `groups.*.json` - Group definitions (1,590 groups)
- `typedogma.*.json` - Dogma attributes for types (26,428 entries)
- `requiredskillsfortypes.*.json` - Skill prerequisites (9,283 entries)

## Usage

1. Ensure Pyfa's FSD data is up to date in the `D:\Works\codes\fanjing\evemon_cn\Pyfa\staticdata\fsd_built` directory

2. Run the converter:
   ```
   python generate_eve_skills_from_pyfa.py
   ```

3. The script generates:
   - `eve-skills-en-US.xml.gzip` - English skill data
   - Output location: `src\EVEMon\bin\Release\net8.0-windows\Resources\`

## Output Statistics

- **Skills**: 591 skills with primaryAttribute
- **Groups**: 25 skill groups
- **Prerequisites**: All skill prerequisites included
- **Attributes**: primaryAttr, secondaryAttr, rank, cost

## Skill Attribute Mapping

| EVE Attr ID | EVEMon Name  |
|-------------|--------------|
| 164         | intelligence |
| 165         | memory       |
| 166         | charisma     |
| 167         | perception   |
| 168         | willpower    |
| 275         | rank (skillTimeConstant) |

## File Format

Output follows EVEMon's XML format:
```xml
<skills>
  <group id="255" name="Gunnery">
    <skill id="3300" name="Gunnery" primaryAttr="perception" secondaryAttr="willpower" rank="1" cost="256000">
      <prereq id="0" name="" level="0"/>
      <AlphaLimit>0</AlphaLimit>
    </skill>
  </group>
</skills>
```

## Dependencies

- Python 3.6+
- Standard library: `json`, `os`, `glob`, `gzip`, `xml.etree.ElementTree`, `xml.dom.minidom`

## Notes

- The script automatically merges multiple `types.*.json` and `typedogma.*.json` files
- Skills are identified by having `primaryAttribute` (attribute ID 180) in their dogma attributes
- All skill names use English (`typeName_en-us`) as source
