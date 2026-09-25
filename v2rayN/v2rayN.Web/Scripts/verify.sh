#!/usr/bin/env bash
set -euo pipefail

web_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
upstream_root="$(dirname -- "$web_root")"
repo_root="$(dirname -- "$upstream_root")"
webui_root="$web_root/WebUI"
output_dir="${1:-$web_root/publish/verify-linux-x64}"

dotnet="${DOTNET:-}"
if [[ "$dotnet" != */* && -n "$dotnet" ]]; then
  dotnet="$(command -v "$dotnet" || true)"
fi
if [[ -z "$dotnet" ]]; then
  if [[ -x "$web_root/.NET/dotnet" ]]; then
    dotnet="$web_root/.NET/dotnet"
  else
    dotnet="$(command -v dotnet || true)"
  fi
fi
if [[ -z "$dotnet" || ! -x "$dotnet" ]]; then
  printf 'Required build tool is not available: dotnet (expected .NET/dotnet or PATH)\n' >&2
  exit 1
fi
command -v npm >/dev/null 2>&1 || { printf 'Required build tool is not available: npm\n' >&2; exit 1; }

export NUGET_PACKAGES="${NUGET_PACKAGES:-$repo_root/.packages/nuget}"
export npm_config_cache="${npm_config_cache:-$repo_root/.packages/npm-cache}"

npm ci --prefix "$webui_root"
npm run build --prefix "$webui_root"
"$dotnet" test "$web_root/Tests/v2rayN.Web.Tests.csproj" --configuration Release
"$dotnet" test "$upstream_root/ServiceLib.Tests/ServiceLib.Tests.csproj" --configuration Release
"$dotnet" publish "$web_root/v2rayN.Web.csproj" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  --output "$output_dir"

mkdir -p "$output_dir/wwwroot"
cp -a "$webui_root/dist/." "$output_dir/wwwroot/"
test -x "$output_dir/v2rayN.Web"
printf 'Web verification passed; native publish ready: %s\n' "$output_dir/v2rayN.Web"
