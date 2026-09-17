import json
import re
import sys

def validate_locale(lang_code):
    with open('Locales/en.json', encoding='utf-8') as f:
        en = json.load(f)

    path = f'Locales/{lang_code}.json'
    try:
        with open(path, encoding='utf-8') as f:
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
    langs = sys.argv[1:] if len(sys.argv) > 1 else ['zh', 'pt', 'ru', 'ar', 'ko', 'it', 'tr', 'pl', 'nl', 'id']
    all_ok = True
    for l in langs:
        ok, msg = validate_locale(l)
        print(f"[{'OK' if ok else 'FAIL'}] {l}: {msg}")
        if not ok:
            all_ok = False
    sys.exit(0 if all_ok else 1)
