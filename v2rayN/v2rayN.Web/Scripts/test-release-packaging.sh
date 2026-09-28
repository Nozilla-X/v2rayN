#!/usr/bin/env bash
# Release packaging dry-run: validates the full-install/app-only package boundaries and the
# web-update.json manifest using small fixtures, without publishing anything.
set -euo pipefail

web_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
temporary="$(mktemp -d)"
trap 'rm -rf "$temporary"' EXIT HUP INT TERM

make_publish_fixture() {
  local rid="$1"
  local publish="$temporary/publish-$rid"
  mkdir -p "$publish/wwwroot/assets" "$publish/bin/xray" "$publish/bin/sing_box" "$publish/bin/mihomo" "$publish/bin/srss"
  printf '#!/bin/sh\nexit 0\n' > "$publish/v2rayN.Web"
  chmod 755 "$publish/v2rayN.Web"
  printf '<html>v2rayN Web</html>\n' > "$publish/wwwroot/index.html"
  printf '{}\n' > "$publish/wwwroot/assets/app.js"
  for executable in bin/xray/xray bin/sing_box/sing-box bin/mihomo/mihomo; do
    printf '#!/bin/sh\nexit 0\n' > "$publish/$executable"
    chmod 755 "$publish/$executable"
  done
  for asset in bin/sing_box/libcronet.so bin/geosite.dat bin/geoip.dat bin/geoip.metadb bin/Country.mmdb bin/srss/geosite-category-ads-all.srs; do
    printf 'asset\n' > "$publish/$asset"
  done
  # User data that must never leak into the app-only self-update package.
  mkdir -p "$publish/guiConfigs" "$publish/guiLogs" "$publish/webData"
  printf 'config\n' > "$publish/guiConfigs/guiNConfig.json"
  printf 'log\n' > "$publish/guiLogs/app.log"
  printf 'auth\n' > "$publish/webData/web-auth.json"
  python3 - "$publish/v2rayN.Web.build.json" "$rid" <<'PY'
import json
import sys

path, rid = sys.argv[1:]
with open(path, "w", encoding="utf-8") as handle:
    json.dump({
        "product": "v2rayN.Web",
        "version": "7.25.3",
        "commit": "0123456789abcdef",
        "buildDate": "2026-09-28T00:00:00Z",
        "rid": rid,
    }, handle, separators=(",", ":"))
    handle.write("\n")
PY
}

for rid in linux-x64 linux-arm64; do
  make_publish_fixture "$rid"
  bash "$web_root/Scripts/package-native.sh" "$rid" "$temporary/publish-$rid" "$temporary/dist"
done

for asset in \
  v2rayN.Web-linux-64.tar.gz \
  v2rayN.Web-linux-arm64.tar.gz \
  v2rayN.Web-app-linux-64.tar.gz \
  v2rayN.Web-app-linux-arm64.tar.gz; do
  test -s "$temporary/dist/$asset"
done

python3 "$web_root/Scripts/web-update-manifest.py" write \
  --version 7.25.3 \
  --repository 2dust/v2rayN \
  --commit 0123456789abcdef \
  --build-date 2026-09-28T00:00:00Z \
  --dist "$temporary/dist" \
  --output "$temporary/dist/web-update.json"
python3 "$web_root/Scripts/web-update-manifest.py" verify \
  --manifest "$temporary/dist/web-update.json" \
  --dist "$temporary/dist" \
  --repository 2dust/v2rayN

python3 - "$temporary/dist/web-update.json" <<'PY'
import hashlib
import json
import sys
from pathlib import Path

manifest_path = Path(sys.argv[1])
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
assert manifest["product"] == "v2rayN.Web", manifest
assert manifest["version"] == "7.25.3", manifest
assert manifest["commit"] == "0123456789abcdef", manifest
assert manifest["buildDate"] == "2026-09-28T00:00:00Z", manifest
expected = {
    "linux-x64": ("v2rayN.Web-app-linux-64.tar.gz", "https://github.com/2dust/v2rayN/releases/download/7.25.3/v2rayN.Web-app-linux-64.tar.gz"),
    "linux-arm64": ("v2rayN.Web-app-linux-arm64.tar.gz", "https://github.com/2dust/v2rayN/releases/download/7.25.3/v2rayN.Web-app-linux-arm64.tar.gz"),
}
packages = {package["rid"]: package for package in manifest["packages"]}
assert set(packages) == set(expected), packages
for rid, (asset, url) in expected.items():
    package = packages[rid]
    assert package["asset"] == asset, package
    assert package["url"] == url, package
    assert url.startswith("https://github.com/2dust/v2rayN/releases/download/7.25.3/"), package
    assert "Nozilla-X" not in url and "web-v" not in url, package
    data = (manifest_path.parent / asset).read_bytes()
    assert package["sha256"] == hashlib.sha256(data).hexdigest(), package
    assert package["size"] == len(data), package
print("web-update.json assertions passed")
PY

# A dev identity must never become a release manifest.
if python3 "$web_root/Scripts/web-update-manifest.py" write \
  --version 7.25.3-dev \
  --repository 2dust/v2rayN \
  --commit 0123456789abcdef \
  --build-date 2026-09-28T00:00:00Z \
  --dist "$temporary/dist" \
  --output "$temporary/dev-update.json" 2>/dev/null; then
  echo "Expected the dev identity manifest to be rejected" >&2
  exit 1
fi
test ! -e "$temporary/dev-update.json"

# A tampered asset must fail verification.
cp -a "$temporary/dist" "$temporary/tampered"
printf 'tampered' >> "$temporary/tampered/v2rayN.Web-app-linux-64.tar.gz"
if python3 "$web_root/Scripts/web-update-manifest.py" verify \
  --manifest "$temporary/tampered/web-update.json" \
  --dist "$temporary/tampered" \
  --repository 2dust/v2rayN 2>/dev/null; then
  echo "Expected the tampered manifest to be rejected" >&2
  exit 1
fi

printf 'Release packaging dry-run passed\n'
