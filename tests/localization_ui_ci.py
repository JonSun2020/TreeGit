"""Use one verified CI application and restore its portable profile afterwards."""
import json
from pathlib import Path
import subprocess
import sys
from qa_profile import IsolatedProfile

exe, out = (Path(arg).resolve() for arg in sys.argv[1:3])
profile = IsolatedProfile(exe, out)
try:
    (profile.data / 'preference.json').write_text(json.dumps({'Locale': 'zh_CN', 'Theme': 'Light',
        'Check4UpdatesOnStartup': False, 'Workspaces': [{'Name': 'Interface review', 'IsActive': True,
        'Repositories': [], 'RestoreOnStartup': True}]}), encoding='utf-8')
    subprocess.run([sys.executable, str(Path(__file__).with_name('localization_ui.py')), str(exe), str(out), '30'], check=True)
    result = json.loads((out / 'results.json').read_text(encoding='utf-8'))
    # Five menu headers are exercised in each language and theme, both opening and closing.
    menu_checks = {f'{locale} {theme} {action} {index}'
                   for locale in ('en_US', 'zh_CN', 'zh_TW') for theme in ('Light', 'Dark', 'Blue')
                   for action in ('menu opens with its outline', 'menu closes without reopening') for index in range(5)}
    assert menu_checks.issubset(result['checks']), sorted(menu_checks.difference(result['checks']))
    assert result['passed'] == len(result['checks']) and result['passed'] >= 537 and result['failure'] is None
    search_out = out / 'search-interaction'
    subprocess.run([sys.executable, str(Path(__file__).with_name('search_interaction.py')), str(exe), str(search_out)], check=True)
    search_result = json.loads((search_out / 'results.json').read_text(encoding='utf-8'))
    assert search_result['passed'] >= 328 and search_result['failure'] is None, search_result
finally:
    profile.close()
