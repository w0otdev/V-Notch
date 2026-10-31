import json
import re
import sys
from pathlib import Path

LOCALES_DIR = Path(__file__).resolve().parent.parent / 'Locales'
EN_PATH = LOCALES_DIR / 'en.json'


def validate_locale(lang_code, locales_dir=LOCALES_DIR):
    en_path = locales_dir / 'en.json'
    if not en_path.is_file():
        return False, f"Baseline locale file not found: {en_path}"
    with open(en_path, encoding='utf-8-sig') as f:
        en = json.load(f)

    path = locales_dir / f'{lang_code}.json'
    try:
        with open(path, encoding='utf-8-sig') as f:
            target = json.load(f)
    except Exception as e:
        return False, f"Failed to load {path}: {e}"

    en_keys = list(en.keys())
    target_keys = list(target.keys())

    missing = set(en_keys) - set(target_keys)
    extra = set(target_keys) - set(en_keys)

    errors = []
    if missing:
        errors.append(f"Missing {len(missing)} keys: {list(missing)[:5]}...")
    if extra:
        errors.append(f"Extra {len(extra)} keys: {list(extra)[:5]}...")
    if en_keys != target_keys:
        errors.append("Key ordering does not match en.json")

    # Check placeholders
    for k in en_keys:
        if k not in target:
            continue
        en_val = en[k]
        target_val = target[k]
        if isinstance(en_val, str) and isinstance(target_val, str):
            en_ph = sorted(re.findall(r'\{[0-9]+\}', en_val))
            target_ph = sorted(re.findall(r'\{[0-9]+\}', target_val))
            if en_ph != target_ph:
                errors.append(f"Key '{k}' placeholder mismatch: en={en_ph}, {lang_code}={target_ph}")

    if errors:
        return False, "\n".join(errors)
    return True, f"{lang_code} passed all checks ({len(target_keys)} keys)"

if __name__ == '__main__':
    locales_dir = Path(__file__).resolve().parent.parent / 'Locales'
    en_path = locales_dir / 'en.json'
    if not en_path.is_file():
        print(f"Baseline locale file not found: {en_path}", file=sys.stderr)
        sys.exit(1)

    langs = (
        sys.argv[1:]
        if len(sys.argv) > 1
        else [p.stem for p in sorted(locales_dir.glob('*.json')) if p.stem != 'en']
    )
    if not langs:
        print("No locale files found to validate.", file=sys.stderr)
        sys.exit(1)

    all_ok = True
    for l in langs:
        ok, msg = validate_locale(l, locales_dir)
        print(f"[{'OK' if ok else 'FAIL'}] {l}: {msg}")
        if not ok:
            all_ok = False
    sys.exit(0 if all_ok else 1)
