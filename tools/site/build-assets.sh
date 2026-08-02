#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
#
# Generates every derivative the website ships: responsive AVIF/WebP, the hero
# poster and its video, the favicon family and the social card. Sources are the
# committed originals in docs/media/ and the app's own icon — nothing is drawn
# by hand, nothing is downloaded at page load.
#
# Run it anywhere the tools exist (Debian: ffmpeg imagemagick webp libavif-bin
# pngquant chromium, plus python3-fonttools for the font subset):
#
#   SRC=/path/to/repo OUT=/path/to/repo/site/assets tools/site/build-assets.sh
#
# It is idempotent: delete OUT and re-run to rebuild from scratch. The hashes of
# every source land in OUT/img/SOURCES.txt so verify.sh can catch drift when
# somebody re-records a demo and forgets this step.

set -euo pipefail

SRC="${SRC:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
OUT="${OUT:-$SRC/site/assets}"
MEDIA="$SRC/docs/media"
ICO="$SRC/src/HaCompanion.App/Assets/app.ico"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

for t in convert ffmpeg cwebp avifenc pngquant; do
    command -v "$t" >/dev/null || { echo "missing tool: $t" >&2; exit 1; }
done

mkdir -p "$OUT"/{img/{hero,panel,tour,brand},icons,og,video,fonts}

# avifenc/cwebp settings: visually lossless on flat UI surfaces at a fraction of
# the bytes. cq-level is the AVIF quantizer (lower = better); 30 keeps 1px
# hairlines and small UI text crisp, which is the whole point of a product shot.
avif() { # avif <in.png> <out.avif> [cq]
    avifenc --min 0 --max 63 -a end-usage=q -a "cq-level=${3:-30}" -a tune=ssim \
            -s 4 -j all "$1" "$2" >/dev/null
}
webp() { # webp <in.png> <out.webp> [q]
    cwebp -quiet -m 6 -q "${3:-82}" "$1" -o "$2"
}

# emit <source.png> <family> <outdir> <cq> <q> <width...>
emit() {
    local src="$1" family="$2" dir="$3" cq="$4" q="$5"; shift 5
    for w in "$@"; do
        convert "$src" -filter Lanczos -resize "${w}x" -strip "$TMP/$family-$w.png"
        avif "$TMP/$family-$w.png" "$dir/$family-$w.avif" "$cq"
        webp "$TMP/$family-$w.png" "$dir/$family-$w.webp" "$q"
    done
}

echo "== screenshots (tour) =="
# Every tab image is normalised onto one 1400x853 canvas so the tab strip can
# crossfade inside a fixed aspect-ratio box: zero layout shift, no letterbox
# visible because the pad colour is the app's own window background.
for name in start ha-dashboards shortcuts automations my-pc settings; do
    convert "$MEDIA/$name.png" -background '#202020' -gravity center \
            -extent 1400x853 -strip "$TMP/$name-src.png"
    # the dashboard shot is photographic (a real Lovelace view) and needs a
    # gentler quantizer than the flat UI tabs, or the wallpaper bands
    case "$name" in
        ha-dashboards) cq=34; q=78 ;;
        *)             cq=30; q=82 ;;
    esac
    emit "$TMP/$name-src.png" "$name" "$OUT/img/tour" "$cq" "$q" 480 720 960 1400
done

echo "== hero poster (frame from the real demo video) =="
VIDEO="$MEDIA/quick-panel-demo.mp4"
# The capture has ~30px of a foreign window at the left edge. Crop ONLY that:
# the panel header touches the top and the taskbar the bottom, so any vertical
# trim cuts into one of them (an earlier 18px top-crop halved the panel title).
# The hero frame simply uses the resulting 1888:1080 ratio. Same crop for
# poster and video, or they would jump when the video fades in.
EDGE="crop=1888:1080:32:0"
# t=6.0 is the panel fully slid in and settled (the hero's LCP image).
ffmpeg -loglevel error -y -ss 6.0 -i "$VIDEO" -frames:v 1 -vf "$EDGE" "$TMP/hero-src.png"
emit "$TMP/hero-src.png" "hero-poster" "$OUT/img/hero" 32 80 960 1280 1888

echo "== quick panel =="
# The screenshot carries ~50px of desktop to the left of the panel. On the page
# the panel is presented as an object in its own right, so that strip is cropped
# away instead of being faked into a border.
convert "$MEDIA/quick-panel.png" -crop +50+0 +repage -strip "$TMP/panel-src.png"
emit "$TMP/panel-src.png" "quick-panel" "$OUT/img/panel" 28 84 510

echo "== video (trimmed to the part that actually shows something) =="
# The 11s original spends its first 3.4s and last 3.8s on an empty desktop. As a
# looping hero that means most visitors stare at wallpaper. Cut to the arrival,
# the pause and the exit: 5.2s that loop cleanly.
CUT="-ss 2.80 -t 5.20"
# shellcheck disable=SC2086
ffmpeg -loglevel error -y $CUT -i "$VIDEO" -an -vf "$EDGE,scale=1440:-2" \
       -c:v libx264 -preset slow -crf 23 -pix_fmt yuv420p -movflags +faststart \
       "$OUT/video/quick-panel-demo.mp4"
# shellcheck disable=SC2086
ffmpeg -loglevel error -y $CUT -i "$VIDEO" -an -vf "$EDGE,scale=1440:-2" \
       -c:v libvpx-vp9 -crf 34 -b:v 0 -row-mt 1 -cpu-used 2 -pix_fmt yuv420p \
       "$OUT/video/quick-panel-demo.webm"
# ship WebM only when it actually beats the MP4 - a second file that is bigger
# than the one it replaces is pure waste
if [ "$(stat -c%s "$OUT/video/quick-panel-demo.webm")" -ge "$(stat -c%s "$OUT/video/quick-panel-demo.mp4")" ]; then
    echo "   WebM larger than MP4 - dropped"
    rm -f "$OUT/video/quick-panel-demo.webm"
fi

echo "== icons from the app's own app.ico =="
# index 5 of the ICO is the 256x256 entry; ImageMagick can address it directly.
convert "$ICO[5]" -strip "$TMP/icon-256.png"
for s in 16 32 48 128 180 192 512; do
    convert "$TMP/icon-256.png" -filter Lanczos -resize "${s}x${s}" -strip "$TMP/i$s.png"
    # the big ones only ever load on "install to home screen" — squeeze harder
    [ "$s" -ge 192 ] && q="55-88" || q="70-95"
    pngquant --quality "$q" --strip --force --output "$OUT/icons/icon-$s.png" "$TMP/i$s.png"
done
cp "$OUT/icons/icon-180.png" "$OUT/icons/apple-touch-icon.png"
convert "$OUT/icons/icon-16.png" "$OUT/icons/icon-32.png" "$OUT/icons/icon-48.png" "$OUT/icons/favicon.ico"
# maskable: Android crops to a circle, so the artwork gets an 80% safe area on
# the icon's own deep blue instead of losing its corners
convert "$TMP/icon-256.png" -resize 410x410 -background '#14599f' -gravity center \
        -extent 512x512 -strip "$TMP/maskable.png"
pngquant --quality 55-88 --strip --force --output "$OUT/icons/icon-maskable-512.png" "$TMP/maskable.png"
for s in 32 64; do
    convert "$TMP/icon-256.png" -filter Lanczos -resize "${s}x${s}" -strip "$TMP/b$s.png"
    pngquant --quality 70-95 --strip --force --output "$OUT/img/brand/logo-$s.png" "$TMP/b$s.png"
done

echo "== social card =="
if command -v chromium >/dev/null; then
    chromium --headless --disable-gpu --no-sandbox --hide-scrollbars \
             --window-size=1200,630 --screenshot="$TMP/og.png" \
             "file://$SRC/tools/site/og.html" >/dev/null 2>&1
    convert "$TMP/og.png" -quality 86 -strip "$OUT/og/og-1200x630.jpg"
else
    echo "   chromium missing - social card skipped"
fi

echo "== source hashes =="
{
    echo "# Generated by tools/site/build-assets.sh - re-run it when any of these change."
    for f in "$MEDIA"/*.png "$MEDIA"/*.mp4 "$ICO"; do
        [ -e "$f" ] || continue
        printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "${f#"$SRC"/}"
    done
} > "$OUT/img/SOURCES.txt"

echo
echo "done. bytes:"
du -sh "$OUT"/* | sort -k2
