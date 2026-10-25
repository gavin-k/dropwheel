#!/usr/bin/env bash
# Downloads the sidecar binaries pinned in scripts/sidecars.lock, checks their SHA-256 and unpacks
# ffmpeg.exe, ffprobe.exe, their DLLs and the license into tools/sidecars/<name>/<arch>/.
# Safe to run again: a folder whose .sha256 marker matches the pin is left alone.
# Usage: scripts/fetch-sidecars.sh [arch...]     (default: every arch in the lock file)
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
ROOT=tools/sidecars
CACHE=$ROOT/.download
mkdir -p "$CACHE"

sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }

grep -v '^#' scripts/sidecars.lock | while read -r name arch url sum; do
  [[ -z "$name" ]] && continue
  if [[ $# -gt 0 && " $* " != *" $arch "* ]]; then continue; fi
  dest=$ROOT/$name/$arch
  if [[ -f $dest/.sha256 && "$(cat "$dest/.sha256")" == "$sum" ]]; then
    echo "==> $name/$arch up to date"
    continue
  fi

  zip=$CACHE/$(basename "$url")
  if [[ ! -f $zip || "$(sha256 "$zip")" != "$sum" ]]; then
    echo "==> downloading $(basename "$url")"
    curl -fsSL --retry 3 -o "$zip.part" "$url"
    mv "$zip.part" "$zip"
  fi
  actual=$(sha256 "$zip")
  if [[ "$actual" != "$sum" ]]; then
    echo "SHA-256 mismatch for $zip: expected $sum, got $actual" >&2
    exit 1
  fi

  work=$(mktemp -d "$CACHE/unpack.XXXXXX")
  unzip -q "$zip" -d "$work"
  top=$(find "$work" -mindepth 1 -maxdepth 1 -type d | head -1)
  rm -rf "$dest"
  mkdir -p "$dest"
  cp "$top"/bin/ffmpeg.exe "$top"/bin/ffprobe.exe "$top"/bin/*.dll "$dest"/
  cp "$top"/LICENSE.txt "$dest"/LICENSE.txt
  build=$(basename "$top")
  printf 'Binary: %s\nSHA-256: %s\nBuilt by: https://github.com/BtbN/FFmpeg-Builds (LGPL variant, shared libraries)\nFFmpeg source: https://github.com/FFmpeg/FFmpeg/commit/%s\n' \
    "$url" "$sum" "$(sed -E 's/^ffmpeg-n?[^-]*-[0-9]+-g([0-9a-f]+)-.*/\1/' <<<"$build")" > "$dest/SOURCE.txt"
  rm -rf "$work"
  echo "$sum" > "$dest/.sha256"
  echo "==> $name/$arch ready in $dest"
done
