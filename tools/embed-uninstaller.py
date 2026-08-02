#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only
"""Keep the uninstaller embedded in install.ps1 identical to uninstall.ps1.

install.ps1 is executed as `irm ... | iex`, so it cannot read a file next to itself —
the uninstaller has to travel inside it. To make sure the two never drift apart, the
embedded copy is generated from uninstall.ps1 by this script.

    python3 tools/embed-uninstaller.py           # update install.ps1
    python3 tools/embed-uninstaller.py --check   # CI: fail if it is out of date
"""
import argparse
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
INSTALL = ROOT / "install.ps1"
UNINSTALL = ROOT / "uninstall.ps1"

# Single-quoted here-string: $ ` and " inside stay literal. Two rules for the payload:
# it must be ASCII (the file is written with -Encoding ASCII) and no line may start
# with '@ (that would close the here-string early).
BLOCK = re.compile(r"(\$uninstallPs1 = @'\n)(.*?)(\n'@\n)", re.DOTALL)


def payload() -> str:
    text = UNINSTALL.read_text(encoding="utf-8")
    if not text.isascii():
        bad = [i + 1 for i, line in enumerate(text.splitlines()) if not line.isascii()]
        sys.exit(f"uninstall.ps1 must be ASCII-only (non-ASCII on lines {bad})")
    if any(line.startswith("'@") for line in text.splitlines()):
        sys.exit("uninstall.ps1 must not contain a line starting with '@ (ends the here-string)")
    return text.rstrip("\n")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="only verify, change nothing")
    args = ap.parse_args()

    install = INSTALL.read_text(encoding="utf-8")
    match = BLOCK.search(install)
    if not match:
        sys.exit("marker $uninstallPs1 = @'...'@ not found in install.ps1")

    wanted = payload()
    if match.group(2) == wanted:
        print("install.ps1: embedded uninstaller is up to date")
        return 0
    if args.check:
        sys.exit("install.ps1 is out of date - run: python3 tools/embed-uninstaller.py")

    INSTALL.write_text(install[: match.start(2)] + wanted + install[match.end(2):], encoding="utf-8")
    print(f"install.ps1: embedded uninstaller updated ({len(wanted.splitlines())} lines)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
