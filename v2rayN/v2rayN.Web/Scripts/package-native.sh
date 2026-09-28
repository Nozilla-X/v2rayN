#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 2 ] || [ "$#" -gt 3 ]; then
  echo "Usage: $0 <linux-x64|linux-arm64> <publish-directory> [dist-directory]" >&2
  exit 2
fi

rid="$1"
publish_dir="$2"
dist_dir="${3:-dist}"

case "$rid" in
  linux-x64) arch="64" ;;
  linux-arm64) arch="arm64" ;;
  *)
    echo "Unsupported runtime identifier: $rid" >&2
    exit 2
    ;;
esac

full_name="v2rayN.Web-linux-$arch.tar.gz"
app_name="v2rayN.Web-app-linux-$arch.tar.gz"

require_file() {
  if [ ! -f "$1" ]; then
    echo "Package is missing required file: $1" >&2
    exit 1
  fi
}

require_executable() {
  if [ ! -x "$1" ]; then
    echo "Package is missing required executable: $1" >&2
    exit 1
  fi
}

require_file "$publish_dir/v2rayN.Web"
require_file "$publish_dir/v2rayN.Web.build.json"
require_file "$publish_dir/wwwroot/index.html"
require_executable "$publish_dir/v2rayN.Web"
require_executable "$publish_dir/bin/xray/xray"
require_executable "$publish_dir/bin/sing_box/sing-box"
require_executable "$publish_dir/bin/mihomo/mihomo"
require_file "$publish_dir/bin/sing_box/libcronet.so"
require_file "$publish_dir/bin/geosite.dat"
require_file "$publish_dir/bin/geoip.dat"
require_file "$publish_dir/bin/geoip.metadb"
require_file "$publish_dir/bin/Country.mmdb"
require_file "$publish_dir/bin/srss/geosite-category-ads-all.srs"

mkdir -p "$dist_dir"
full_archive="$dist_dir/$full_name"
app_archive="$dist_dir/$app_name"
list_dir="$(mktemp -d)"
staging_dir="$(mktemp -d)"
trap 'rm -rf "$list_dir" "$staging_dir"' EXIT HUP INT TERM

# 1. full install: the fresh-install package with the complete upstream Core bundle.
tar -C "$publish_dir" -czf "$full_archive" .
tar -tzf "$full_archive" > "$list_dir/full.contents"
for entry in ./v2rayN.Web ./v2rayN.Web.build.json ./wwwroot/index.html ./bin/xray/xray ./bin/sing_box/sing-box ./bin/mihomo/mihomo; do
  if ! grep -Fxq "$entry" "$list_dir/full.contents"; then
    echo "Full install package is missing archive entry: $entry" >&2
    exit 1
  fi
done

# 2. app-only self-update package: executable, build identity, and WebUI only.
install -m 755 "$publish_dir/v2rayN.Web" "$staging_dir/v2rayN.Web"
install -m 644 "$publish_dir/v2rayN.Web.build.json" "$staging_dir/v2rayN.Web.build.json"
cp -a "$publish_dir/wwwroot" "$staging_dir/wwwroot"
tar -C "$staging_dir" -czf "$app_archive" v2rayN.Web v2rayN.Web.build.json wwwroot
tar -tzf "$app_archive" > "$list_dir/app.contents"
for entry in v2rayN.Web v2rayN.Web.build.json wwwroot/index.html; do
  if ! grep -Fxq "$entry" "$list_dir/app.contents"; then
    echo "App-only package is missing archive entry: $entry" >&2
    exit 1
  fi
done
if grep -Eq '(^|/)(bin|guiConfigs|guiLogs|webData|web-auth\.json)(/|$)|web-auth\.json$' "$list_dir/app.contents"; then
  echo "App-only package must not contain Core binaries, logs, or user data:" >&2
  grep -E '(^|/)(bin|guiConfigs|guiLogs|webData|web-auth\.json)(/|$)|web-auth\.json$' "$list_dir/app.contents" >&2
  exit 1
fi
if tar -tvzf "$app_archive" | grep -Eq '^l'; then
  echo "App-only package must not contain symbolic links." >&2
  exit 1
fi

printf 'Packaged %s and %s\n' "$full_archive" "$app_archive"
