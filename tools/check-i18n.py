#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only
"""Guards Assets/i18n/strings.json: every language must mirror the English block.

A missing key does not crash the app - the indexer silently falls back to
English - which is exactly why drift would go unnoticed without this check.
Verified: identical key sets AND order, {N}-placeholder parity, no empty values.

Run (also wired into CI next to embed-uninstaller.py):

    python3 tools/check-i18n.py
"""
import json
import pathlib
import re
import sys

PATH = (pathlib.Path(__file__).resolve().parent.parent
        / "src" / "HaCompanion.App" / "Assets" / "i18n" / "strings.json")


def placeholders(value: str) -> list[str]:
    return sorted(re.findall(r"\{\d\}", value))


def main() -> int:
    data = json.loads(PATH.read_text(encoding="utf-8"))
    en = data.get("en")
    if not en:
        print("i18n: missing 'en' block", file=sys.stderr)
        return 1

    errors = []
    for lang, block in data.items():
        if lang == "en":
            continue
        if list(block) != list(en):
            missing = [k for k in en if k not in block]
            extra = [k for k in block if k not in en]
            for k in missing:
                errors.append(f"{lang}: missing key {k}")
            for k in extra:
                errors.append(f"{lang}: extra key {k}")
            if not missing and not extra:
                errors.append(f"{lang}: key order differs from en")
        for key, value in block.items():
            if key in en and placeholders(value) != placeholders(en[key]):
                errors.append(f"{lang}.{key}: placeholders {placeholders(value)}"
                              f" != en {placeholders(en[key])}")
            if not str(value).strip():
                errors.append(f"{lang}.{key}: empty value")

    for e in errors:
        print(f"i18n: {e}", file=sys.stderr)
    if errors:
        return 1
    print(f"i18n: {len(data)} languages x {len(en)} keys - consistent")
    return 0


if __name__ == "__main__":
    sys.exit(main())
