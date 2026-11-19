"""Check the shipped language catalogs and literal UI resource references."""
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
key = '{http://schemas.microsoft.com/winfx/2006/xaml}Key'
locales = ['en_US', 'zh_CN', 'zh_TW']
catalogs = {}
for locale in locales:
    elements = [element for element in ET.parse(root / f'src/Resources/Locales/{locale}.axaml').getroot() if key in element.attrib]
    assert len({element.attrib[key] for element in elements}) == len(elements), f'Duplicate keys: {locale}'
    catalogs[locale] = {element.attrib[key]: element.text or '' for element in elements}
assert {path.stem for path in (root / 'src/Resources/Locales').glob('*.axaml')} == set(locales)
base = catalogs['en_US']
for locale, catalog in catalogs.items():
    assert catalog.keys() == base.keys(), (locale, catalog.keys() ^ base.keys())
    for name, text in catalog.items():
        assert text.strip(), (locale, name, 'empty text')
        if name.startswith('Text.TreeGit.Operation.'):
            assert '\ufffd' not in text and not re.fullmatch(r'[?\s]+',text), (locale,name,'damaged operation text')
        # Escaped braces are literal UI examples, not format arguments.
        placeholders = lambda value: sorted(re.findall(r'(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})', value))
        assert placeholders(text) == placeholders(base[name]), (locale, name, 'different format arguments')
# Equal key sets cannot catch an untranslated English fallback. Keep only
# deliberate product names and Git-side labels shared with the English UI.
shared_technical_labels = {'TreeGit Askpass', 'AI', 'Git LFS', 'Git Flow', 'Local', 'Remote', 'Base', 'MINE', 'THEIRS'}
untranslated = []
simplified_in_traditional = []
for locale in ['zh_CN', 'zh_TW']:
    for name, text in catalogs[locale].items():
        if text == base[name] and re.search(r'[A-Za-z]', text) and not re.search(r'[\u3400-\u9fff]', text) and text not in shared_technical_labels:
            untranslated.append((locale, name))
        # These characters have distinct, unambiguous forms in this UI's
        # traditional catalog; do not classify ambiguous shared Han characters.
        if locale == 'zh_TW' and re.search('[这为显选库页设开关变处归传输无创储误经层级宽对线动图标检签暂时错]', text):
            simplified_in_traditional.append(name)
assert not untranslated, ('Untranslated English UI text', len(untranslated), untranslated[:12])
assert not simplified_in_traditional, ('Simplified Chinese in traditional UI', len(simplified_in_traditional), simplified_in_traditional[:12])
missing = []
references = 0
for path in (root / 'src').rglob('*'):
    if path.suffix not in ['.cs', '.axaml'] or set(path.parts) & {'bin', 'obj'}: continue
    text = path.read_text(encoding='utf-8-sig')
    names = re.findall(r'App\.(?:Text|TextBinding)\("([^"]+)"\s*[,)]', text)
    names += re.findall(r'App\.BindText\([^\n]+?,\s*"([^"]+)"\)', text)
    names += re.findall(r'\{(?:DynamicResource|StaticResource) Text\.([^}]+)\}', text)
    for name in names:
        references += 1
        if 'Text.' + name not in base: missing.append({'file': str(path.relative_to(root)), 'key': name})
assert not missing, json.dumps(missing, indent=2)
report = {'locales': locales, 'keysPerLocale': len(base), 'literalReferences': references, 'missing': missing,
          'untranslatedEnglish': untranslated, 'simplifiedInTraditional': simplified_in_traditional}
if len(sys.argv) > 1:
    Path(sys.argv[1]).write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report))
