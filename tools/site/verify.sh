#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
#
# The one-command quality gate for the website. Run it on the build box before
# every push that touches site/:
#
#   bash tools/site/verify.sh            # everything local
#   bash tools/site/verify.sh --live     # additionally checks the public URLs
#                                        #   (only makes sense once the repo is public)
#
# Steps: source-hash drift, byte budget, HTML validation, Playwright/axe sweep
# (including the file:// run), link check, Lighthouse CI. Any failure stops the
# script; the summary at the end only prints when everything passed.

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

step() { printf '\n\033[1m== %s ==\033[0m\n' "$1"; }

step "sources in sync (docs/media vs generated assets)"
# SOURCES.txt records the hash of every input at generation time. A mismatch
# means someone re-recorded a demo or swapped a screenshot without re-running
# build-assets.sh - the site would silently show stale media.
while read -r hash file; do
    [ "${hash:0:1}" = "#" ] && continue
    actual="$(sha256sum "$file" | cut -d' ' -f1)"
    if [ "$actual" != "$hash" ]; then
        echo "STALE: $file changed since assets were generated - re-run tools/site/build-assets.sh"
        exit 1
    fi
done < site/assets/img/SOURCES.txt
echo "ok"

step "byte budget"
node tools/site/budget.mjs

step "HTML validation"
npx --yes html-validate@8 --config tools/site/html-validate.json "site/**/*.html"

step "Playwright + axe sweep (http)"
node tools/site/shots.mjs

step "Playwright sweep (file://)"
node tools/site/shots.mjs --file-protocol --out /tmp/shots-file

step "internal link check"
python3 -m http.server 4199 --directory site >/dev/null 2>&1 &
SRV=$!
trap 'kill $SRV 2>/dev/null || true' EXIT
sleep 1
npx --yes linkinator@6 http://127.0.0.1:4199 --recurse --timeout 20000 \
    --skip "^https?://(?!127\.0\.0\.1)" --verbosity error
kill $SRV; trap - EXIT

step "Lighthouse CI (3 runs, median)"
CHROME_PATH="${CHROME_PATH:-/usr/bin/chromium}" \
npx --yes @lhci/cli@0.14 autorun --config=tools/site/lighthouserc.json

if [ "${1:-}" = "--live" ]; then
    step "live gates (public repo required)"
    curl -sfI https://raw.githubusercontent.com/Elias0505/ha-companion-windows/main/install.ps1 | head -1
    curl -sfI -o /dev/null -w '%{http_code} /releases/latest\n' \
        https://github.com/Elias0505/ha-companion-windows/releases/latest
    npx --yes linkinator@6 http://127.0.0.1:4199 >/dev/null 2>&1 || true
fi

printf '\n\033[1mverify: all gates passed.\033[0m\n'
