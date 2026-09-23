#!/usr/bin/env bash
set -euo pipefail

web_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
upstream_root="$(dirname -- "$web_root")"
repo_root="$(dirname -- "$upstream_root")"
webui_root="$web_root/WebUI"
rid="${1:-linux-x64}"
output_dir="${2:-$web_root/publish/$rid}"

for tool in dotnet npm; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    printf 'Required build tool is not available: %s\n' "$tool" >&2
    exit 1
  fi
done

case "$rid" in
  linux-x64|linux-arm64) ;;
  *) printf 'Supported runtime identifiers: linux-x64, linux-arm64 (received %s)\n' "$rid" >&2; exit 2 ;;
esac

export NUGET_PACKAGES="${NUGET_PACKAGES:-$repo_root/.packages/nuget}"
export npm_config_cache="${npm_config_cache:-$repo_root/.packages/npm-cache}"

npm ci --prefix "$webui_root"
npm run build --prefix "$webui_root"
dotnet publish "$web_root/v2rayN.Web.csproj" \
  --configuration Release \
  --runtime "$rid" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  --output "$output_dir"

mkdir -p "$output_dir/wwwroot"
cp -a "$webui_root/dist/." "$output_dir/wwwroot/"
test -x "$output_dir/v2rayN.Web"
printf 'Native publish ready: %s\n' "$output_dir/v2rayN.Web"
