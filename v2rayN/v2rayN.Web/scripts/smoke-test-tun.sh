#!/usr/bin/env bash
set -euo pipefail

: "${V2RAYN_WEB_API_KEY:?Set V2RAYN_WEB_API_KEY management key}"
base_url="${V2RAYN_WEB_URL:-http://127.0.0.1:5080}"
base_url="${base_url%/}"
login_response="$(jq -cn '{key: env.V2RAYN_WEB_API_KEY}' | curl --fail --silent --show-error \
  -H 'Content-Type: application/json' \
  --data-binary @- \
  "$base_url/api/auth/login")"
session_token="$(jq -er '.data.token' <<<"$login_response")"
unset login_response
unset V2RAYN_WEB_API_KEY
temp_dir="$(mktemp -d)"
settings_file="$temp_dir/tun.json"
was_running=false
changed_settings=false
original_enabled=""
original_legacy_protect=""

api() {
  curl --fail --silent --show-error \
    -H "Authorization: Bearer $session_token" \
    -H 'Content-Type: application/json' "$@"
}

restore_state() {
  if [[ "$changed_settings" == true && -f "$settings_file" ]]; then
    local payload
    if [[ -n "$original_enabled" && -n "$original_legacy_protect" ]]; then
      payload="$(jq --argjson enabled "$original_enabled" --argjson protect "$original_legacy_protect" '.data | .enabled = $enabled | .enableLegacyProtect = $protect' "$settings_file")"
      api -X PUT "$base_url/api/settings/tun" --data-binary "$payload" >/dev/null || true
    fi
  fi
  if [[ "$was_running" == false ]]; then
    api -X POST "$base_url/api/core/stop" >/dev/null || true
  fi
  rm -rf "$temp_dir"
}
trap restore_state EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

status_before="$(api "$base_url/api/status")"
profile_id="$(jq -r '.data.currentProfileId // empty' <<<"$status_before")"
if [[ "$(jq -r '.data.coreRunning' <<<"$status_before")" == true ]]; then
  was_running=true
fi
if [[ -z "$profile_id" ]]; then
  echo "Select a profile before running the TUN smoke test." >&2
  exit 1
fi
api "$base_url/api/settings/tun" >"$settings_file"
if [[ "$(jq -r '.data.capabilityAvailable' "$settings_file")" != true ]]; then
  echo "The Web process does not have usable /dev/net/tun + CAP_NET_ADMIN." >&2
  exit 1
fi

original_enabled="$(jq -r '.data.enabled' "$settings_file")"
original_legacy_protect="$(jq -r '.data.enableLegacyProtect' "$settings_file")"
changed_settings=true
payload="$(jq '.data | .enabled = true | .enableLegacyProtect = false' "$settings_file")"
api -X PUT "$base_url/api/settings/tun" --data-binary "$payload" >/dev/null
if [[ "$was_running" == false ]]; then
  api -X POST "$base_url/api/core/start" >/dev/null
fi

current_status='{}'
for _ in {1..30}; do
  current_status="$(api "$base_url/api/status")"
  [[ "$(jq -r '.data.coreRunning' <<<"$current_status")" == true ]] || { sleep 1; continue; }
  [[ "$(jq -r '.data.tunInterfaceActive' <<<"$current_status")" == true ]] && break
  sleep 1
done

interface="$(jq -r '.data.tunInterfaceName // empty' <<<"$current_status")"
if [[ -z "$interface" || "$(jq -r '.data.tunInterfaceActive' <<<"$current_status")" != true ]]; then
  echo "Core did not create a TUN interface in this network namespace." >&2
  exit 1
fi

echo "TUN smoke test passed: Core is running and /sys/class/net/$interface exists."

# Restore the original TUN preferences; the EXIT trap stops Core if this script started it.
payload="$(jq --argjson enabled "$original_enabled" --argjson protect "$original_legacy_protect" '.data | .enabled = $enabled | .enableLegacyProtect = $protect' "$settings_file")"
api -X PUT "$base_url/api/settings/tun" --data-binary "$payload" >/dev/null
changed_settings=false
