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

# The embedded v2rayN.Web.build.json must match the manifest, and the app-only layout
# boundary must reject forbidden files, links, duplicates, and path traversal. Each variant
# regenerates the manifest from the tampered archive so SHA-256 and size stay self-consistent
# and only the identity/layout cross-check can fail.
python3 - "$temporary/dist" "$temporary/identity-tamper" <<'PY'
import io
import json
import shutil
import sys
import tarfile
from copy import copy
from pathlib import Path

dist = Path(sys.argv[1])
out_root = Path(sys.argv[2])
source = dist / "v2rayN.Web-app-linux-64.tar.gz"
identity_name = "v2rayN.Web.build.json"

with tarfile.open(source, "r:gz") as archive:
    base = [
        (member, archive.extractfile(member).read() if member.isfile() else None)
        for member in archive.getmembers()
    ]

identity = json.loads(next(data for member, data in base if member.name == identity_name))
ui_index = next(data for member, data in base if member.name == "wwwroot/index.html")
untouched = [path for path in dist.iterdir() if path.is_file() and path.name.endswith(".tar.gz") and path != source]


def write_variant(name, transform):
    target = out_root / name
    target.mkdir(parents=True, exist_ok=True)
    for path in untouched:
        shutil.copy2(path, target / path.name)
    with tarfile.open(target / source.name, "w:gz") as archive:
        for member, data in transform(list(base)):
            if data is not None and len(data) != member.size:
                member = copy(member)
                member.size = len(data)
            archive.addfile(member, io.BytesIO(data) if data is not None else None)


def identity_transform(field=None, value=None, drop=False, invalid=False):
    def transform(members):
        result = []
        for member, data in members:
            if member.name != identity_name:
                result.append((member, data))
                continue
            if drop:
                continue
            if invalid:
                result.append((member, b"{ not valid json"))
                continue
            changed = dict(identity)
            changed[field] = value
            result.append((member, json.dumps(changed, separators=(",", ":")).encode("utf-8")))
        return result
    return transform


def appended(name, data, member_type=tarfile.REGTYPE, linkname=""):
    def transform(members):
        member = tarfile.TarInfo(name)
        member.type = member_type
        if member_type == tarfile.REGTYPE:
            member.size = len(data)
            member.mode = 0o755
            return members + [(member, data)]
        member.linkname = linkname
        member.mode = 0o777
        return members + [(member, None)]
    return transform


write_variant("version", identity_transform(field="version", value="7.25.4"))
write_variant("commit", identity_transform(field="commit", value="ffffffffffffffff"))
write_variant("build-date", identity_transform(field="buildDate", value="2026-09-29T00:00:00Z"))
write_variant("rid", identity_transform(field="rid", value="linux-arm64"))
write_variant("missing-identity", identity_transform(drop=True))
write_variant("invalid-json", identity_transform(invalid=True))
write_variant("embedded-bin", appended("bin/xray/xray", b"#!/bin/sh\nexit 0\n"))
write_variant("symlink", appended("wwwroot/vendor.js", b"", tarfile.SYMTYPE, "/etc/passwd"))
write_variant("duplicate", appended("wwwroot/index.html", ui_index))
write_variant("traversal", appended("../outside.txt", b"nope"))
print(f"prepared identity tamper variants in {out_root}")
PY

for variant in \
  version commit build-date rid missing-identity invalid-json \
  embedded-bin symlink duplicate traversal; do
  if python3 "$web_root/Scripts/web-update-manifest.py" write \
    --version 7.25.3 \
    --repository 2dust/v2rayN \
    --commit 0123456789abcdef \
    --build-date 2026-09-28T00:00:00Z \
    --dist "$temporary/identity-tamper/$variant" \
    --output "$temporary/identity-tamper/$variant/web-update.json" \
    2>"$temporary/identity-tamper/$variant.log"; then
    echo "Expected the $variant identity tamper to be rejected" >&2
    exit 1
  fi
  if ! grep -q "app-only archive v2rayN.Web-app-linux-64.tar.gz" "$temporary/identity-tamper/$variant.log"; then
    echo "The $variant identity tamper was rejected without naming the app-only archive" >&2
    cat "$temporary/identity-tamper/$variant.log" >&2
    exit 1
  fi
done

printf 'Release packaging dry-run passed\n'
