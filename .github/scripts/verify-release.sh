#!/usr/bin/env bash
set -uo pipefail
TAG="$1"; REPO="${2:-beacontower/tools}"
product="${TAG%%-*}"
version="${TAG#*-}"; version="${version#v}"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
echo "tag=$TAG product=$product version=$version"

gh release download "$TAG" -R "$REPO" -D "$work" --clobber >/dev/null 2>&1
count=$(find "$work" -type f | wc -l)
if [ "$count" -eq 0 ]; then echo "FAIL: release $TAG publishes no assets"; exit 1; fi
echo "  assets: $count"

rc=0
if [ -f "$work/checksums.txt" ]; then
  if (cd "$work" && sha256sum -c checksums.txt >/dev/null 2>&1); then echo "  checksums: OK"
  else echo "FAIL: checksums.txt does not match the published assets"; rc=1; fi
else
  echo "  checksums: none published"
fi

bin=""
for cand in "$work/$product-linux-amd64" "$work/$product-linux-x64"; do
  [ -f "$cand" ] && { bin="$cand"; break; }
done
if [ -z "$bin" ]; then
  echo "  binary: no bare linux binary in this release, version not asserted"
  exit $rc
fi
chmod +x "$bin"
out=""
for flag in version --version; do
  if out=$("$bin" "$flag" 2>&1) && [ -n "$out" ]; then break; fi
  out=""
done
if [ -z "$out" ]; then echo "FAIL: $(basename "$bin") reports no version"; exit 1; fi
out=$(printf '%s' "$out" | head -1)
echo "  binary: $(basename "$bin") -> $out"
if printf '%s' "$out" | grep -qF "$version"; then echo "  version: matches tag"
else echo "FAIL: binary reports '$out', tag says '$version'"; rc=1; fi
exit $rc
