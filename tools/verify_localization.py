"""Check every app-owned string against the bilingual catalog and reviewed technical literals."""
import json
import re
import subprocess
import sys
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
catalog = json.loads((ROOT / 'src/RobloxMultiManager.Core/Localization.en.json').read_text(encoding='utf-8'))
technical = set(json.loads((ROOT / 'tools/localization-technical-allowlist.json').read_text(encoding='utf-8')))
audit = ROOT / 'dist/_localization-audit.json'
audit.parent.mkdir(exist_ok=True)
subprocess.run(['dotnet', 'run', '--project', str(ROOT / 'tools/Localize'), '--', 'extract', str(ROOT), str(audit)], check=True)
issues = []
for item in json.loads(audit.read_text(encoding='utf-8')):
    if item.get('localized'):
        if item['text'] not in catalog:
            issues.append(f"Missing C# translation at {item['file']}:{item['line']}: {item['text']!r}")
    elif item['text'] not in technical:
        issues.append(f"Unlocalized C# literal at {item['file']}:{item['line']}: {item['text'][:100]!r}")

neutral_ui = {'roblox.com', 'OK', 'MultiJanela', 'R', 'English', 'Português (Brasil)', 'LIVE', '×', '#98B6FF', '#65DAC8', '#B9ADFF', 'Segoe UI', 'Arial', 'Tahoma', 'Verdana', 'HH:mm', 'FPS', 'CPU', 'RAM', 'VRAM'}
for path in (ROOT / 'src/RobloxMultiManager').glob('*.xaml'):
    for element in ET.parse(path).getroot().iter():
        for name, value in element.attrib.items():
            if name not in {'Text', 'Content', 'Header', 'Title', 'ToolTip', 'AutomationProperties.Name'} and not (name == 'Value' and element.get('Property') in {'Text', 'Content', 'Header', 'ToolTip'}):
                continue
            for match in re.finditer(r"\{local:Tr '((?:\\.|[^'])*)'\}", value):
                key = match[1].replace("\\'", "'").replace('\\\\', '\\')
                if key not in catalog:
                    issues.append(f'Missing XAML translation in {path.name}: {key!r}')
            if value and not value.startswith('{') and any(char.isalpha() for char in value) and value not in neutral_ui:
                issues.append(f'Unlocalized XAML text in {path.name}: {value!r}')

for source, english in catalog.items():
    if not english:
        issues.append(f'Empty English translation: {source!r}')
    if set(re.findall(r'\{(\d+)(?:[,}:])', source)) != set(re.findall(r'\{(\d+)(?:[,}:])', english)):
        issues.append(f'Changed format arguments: {source!r}')
    if any(marker in source or marker in english for marker in ('Ã£', 'Ã§', 'Ã©', 'Â·', '\ufffd')):
        issues.append(f'Invalid text encoding: {source!r}')
if issues:
    print('\n'.join(issues))
    sys.exit(1)
print(f'Localization verified: {len(catalog)} bilingual entries; all C# and XAML text is localized or explicitly classified as technical/user data.')
