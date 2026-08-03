#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
#
# Deploys site/ to Cloudflare Pages (project "hacompanion" -> hacompanion.com).
#
# Needs:
#   CLOUDFLARE_API_TOKEN   token with  Account > Cloudflare Pages: Edit
#                          and (for the one-time domain setup)
#                          Zone > DNS: Edit  +  Zone > Zone: Read  on hacompanion.com
#   CLOUDFLARE_ACCOUNT_ID  from the dashboard right sidebar (or any `wrangler whoami`)
#
# One-time setup (project + custom domains + DNS) happens automatically when
# missing; every later run is just an upload. Idempotent, safe to re-run.
#
# The committed site targets TWO hosts: hacompanion.com (canonical, root) and
# the GitHub-Pages mirror (project subpath). Everything is relative except
# 404.html, which must use absolute paths because Pages serves it at any
# depth - the committed copy carries the subpath prefix for the mirror, and
# this script rewrites it to the root for Cloudflare.

set -euo pipefail

SRC="${SRC:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
PROJECT="hacompanion"
APEX="hacompanion.com"

: "${CLOUDFLARE_API_TOKEN:?set CLOUDFLARE_API_TOKEN}"
: "${CLOUDFLARE_ACCOUNT_ID:?set CLOUDFLARE_ACCOUNT_ID}"

api() { # api <method> <path> [json]
    curl -sS -X "$1" "https://api.cloudflare.com/client/v4$2" \
         -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
         -H "Content-Type: application/json" \
         ${3:+--data "$3"}
}

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
rsync -a --exclude '.DS_Store' "$SRC/site/" "$STAGE/"

# root-relative 404 + www -> apex redirect (Pages-only files, never committed)
sed -i 's|/ha-companion-windows/|/|g' "$STAGE/404.html"
printf 'https://www.%s/* https://%s/:splat 301!\n' "$APEX" "$APEX" > "$STAGE/_redirects"

echo "== project =="
if ! api GET "/accounts/$CLOUDFLARE_ACCOUNT_ID/pages/projects/$PROJECT" | grep -q '"success": *true'; then
    api POST "/accounts/$CLOUDFLARE_ACCOUNT_ID/pages/projects" \
        "{\"name\":\"$PROJECT\",\"production_branch\":\"main\"}" >/dev/null
    echo "   created"
else
    echo "   exists"
fi

echo "== deploy =="
npx --yes wrangler@4 pages deploy "$STAGE" --project-name "$PROJECT" --branch main --commit-dirty=true

echo "== custom domains =="
for d in "$APEX" "www.$APEX"; do
    api POST "/accounts/$CLOUDFLARE_ACCOUNT_ID/pages/projects/$PROJECT/domains" \
        "{\"name\":\"$d\"}" | grep -o '"success": *[a-z]*' | head -1 | sed "s/^/   $d: /"
done

echo "== DNS (CNAME -> $PROJECT.pages.dev, proxied) =="
ZONE_ID=$(api GET "/zones?name=$APEX" | python3 -c "import json,sys; r=json.load(sys.stdin)['result']; print(r[0]['id'] if r else '')")
if [ -z "$ZONE_ID" ]; then
    echo "   zone $APEX not found in this account - add the domain to Cloudflare first" >&2
    exit 1
fi
for d in "$APEX" "www.$APEX"; do
    # the zone came with placeholder A/AAAA records - clear everything on the
    # name first, a CNAME cannot coexist with them
    for id in $(api GET "/zones/$ZONE_ID/dns_records?name=$d&per_page=100" \
                | python3 -c "import json,sys; [print(r['id']) for r in json.load(sys.stdin)['result'] if r['type'] in ('A','AAAA','CNAME')]"); do
        api DELETE "/zones/$ZONE_ID/dns_records/$id" >/dev/null
    done
    api POST "/zones/$ZONE_ID/dns_records" \
        "{\"type\":\"CNAME\",\"name\":\"$d\",\"content\":\"$PROJECT.pages.dev\",\"proxied\":true}" >/dev/null \
        && echo "   $d: CNAME -> $PROJECT.pages.dev"
done

echo
echo "done: https://$APEX/ (certificate provisioning can take a few minutes on first run)"
