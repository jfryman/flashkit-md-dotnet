#!/usr/bin/env bash
# Regenerates the <releases> block in the Flatpak AppStream metainfo from
# CHANGELOG.md, which is the single source of truth for releases. Software
# centers (KDE Discover, GNOME Software) display the newest <release>
# entry as the app version, so the CI and release workflows run this
# before flatpak-builder; run it manually after cutting a release to keep
# the checked-in copy current.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
changelog="$root/CHANGELOG.md"
metainfo="$root/packaging/flatpak/io.github.jfryman.FlashKitMD.metainfo.xml"

month_num() {
  case "$1" in
    January) echo 01 ;; February) echo 02 ;; March) echo 03 ;;
    April) echo 04 ;; May) echo 05 ;; June) echo 06 ;;
    July) echo 07 ;; August) echo 08 ;; September) echo 09 ;;
    October) echo 10 ;; November) echo 11 ;; December) echo 12 ;;
    *) echo "unknown month '$1' in $changelog" >&2; return 1 ;;
  esac
}

releases_file="$(mktemp)"
trap 'rm -f "$releases_file"' EXIT

# Matches the changelog heading convention: ## X.Y.Z (Month D, YYYY)
heading='^## ([0-9]+\.[0-9]+\.[0-9]+) \(([A-Za-z]+) ([0-9]+), ([0-9]{4})\)$'
while IFS= read -r line; do
  [[ $line =~ $heading ]] || continue
  version="${BASH_REMATCH[1]}"
  month="$(month_num "${BASH_REMATCH[2]}")"
  printf '    <release version="%s" date="%s-%s-%02d" />\n' \
    "$version" "${BASH_REMATCH[4]}" "$month" "${BASH_REMATCH[3]}"
done <"$changelog" >"$releases_file"

if ! [[ -s $releases_file ]]; then
  echo "no release headings matching '## X.Y.Z (Month D, YYYY)' found in $changelog" >&2
  exit 1
fi

awk -v relfile="$releases_file" '
  /<releases>/  { print; while ((getline l < relfile) > 0) print l; skip = 1; next }
  /<\/releases>/ { skip = 0 }
  !skip
' "$metainfo" >"$metainfo.tmp"
mv "$metainfo.tmp" "$metainfo"

count="$(grep -c '<release ' "$metainfo")"
echo "wrote $count releases to $metainfo"
head -n1 "$releases_file"
