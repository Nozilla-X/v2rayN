#!/usr/bin/env bash
set -euo pipefail

executable="${1:?Usage: test-startup-security.sh <native-v2rayN.Web-executable>}"
executable="$(realpath "$executable")"
test -x "$executable"
command -v curl >/dev/null
command -v python3 >/dev/null

temporary="$(mktemp -d)"
web_pid=""
cleanup() {
  if [[ -n "$web_pid" ]] && kill -0 "$web_pid" 2>/dev/null; then
    kill -TERM "$web_pid" 2>/dev/null || true
    for _ in {1..20}; do
      state="$(ps -o stat= -p "$web_pid" 2>/dev/null | tr -d ' ' || true)"
      [[ -z "$state" || "$state" == *Z* ]] && break
      sleep 0.1
    done
    kill -KILL "$web_pid" 2>/dev/null || true
    wait "$web_pid" 2>/dev/null || true
  fi
  rm -rf "$temporary"
}
trap cleanup EXIT HUP INT TERM

new_port() {
  python3 - <<'PY'
import socket

with socket.socket() as sock:
    sock.bind(("127.0.0.1", 0))
    print(sock.getsockname()[1])
PY
}

assert_rejected_without_key() {
  local deployment="$1" data_home="$temporary/$1-data" output="$temporary/$1-rejected.log" status
  local markers=()
  case "$deployment" in
    systemd) markers=(INVOCATION_ID=startup-security-test) ;;
    container) markers=(DOTNET_RUNNING_IN_CONTAINER=true) ;;
    *) echo "Unknown deployment mode: $deployment" >&2; exit 2 ;;
  esac

  set +e
  env -u V2RAYN_WEB_API_KEY \
    -u INVOCATION_ID -u JOURNAL_STREAM -u NOTIFY_SOCKET \
    -u DOTNET_RUNNING_IN_CONTAINER -u container \
    "${markers[@]}" V2RAYN_DATA_HOME="$data_home" \
    "$executable" --foreground >"$output" 2>&1
  status=$?
  set -e

  [[ "$status" -eq 1 ]] || { cat "$output" >&2; echo "$deployment did not fail closed (exit $status)." >&2; exit 1; }
  grep -Fqx 'Management Key is required in supervised/container deployments. Set V2RAYN_WEB_API_KEY before starting v2rayN.Web.' "$output"
  test ! -e "$data_home"
}

wait_for_health() {
  local base_url="$1" output="$2" healthy=false
  for _ in {1..60}; do
    if ! kill -0 "$web_pid" 2>/dev/null; then
      cat "$output" >&2
      echo "Web process exited before becoming healthy." >&2
      exit 1
    fi
    if curl --silent --show-error --fail "$base_url/api/health" -o "$temporary/health.json" 2>/dev/null \
        && python3 - "$temporary/health.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    assert json.load(handle) == {"status": "ok"}
PY
    then
      healthy=true
      break
    fi
    sleep 0.5
  done
  if [[ "$healthy" != true ]]; then
    cat "$output" >&2
    echo "Web process did not become healthy." >&2
    exit 1
  fi
}

stop_instance() {
  local output="$1" stopped=false state exit_code
  kill -TERM "$web_pid"
  for _ in {1..80}; do
    state="$(ps -o stat= -p "$web_pid" 2>/dev/null | tr -d ' ' || true)"
    if [[ -z "$state" || "$state" == *Z* ]]; then
      stopped=true
      break
    fi
    sleep 0.25
  done
  if [[ "$stopped" != true ]]; then
    cat "$output" >&2
    echo "Web process did not stop gracefully." >&2
    exit 1
  fi
  set +e
  wait "$web_pid"
  exit_code=$?
  set -e
  web_pid=""
  if [[ "$exit_code" -ne 0 ]]; then
    cat "$output" >&2
    echo "Web process exited with status $exit_code." >&2
    exit 1
  fi
}

assert_rejected_without_key systemd
assert_rejected_without_key container

# Native interactive first run remains available without SSH or a preconfigured key.
native_port="$(new_port)"
native_data="$temporary/native-data"
native_log="$temporary/native-first-run.log"
native_url="http://127.0.0.1:$native_port"
native_key="first-run-$(python3 -c 'import secrets; print(secrets.token_hex(24))')"
env -u V2RAYN_WEB_API_KEY \
  -u INVOCATION_ID -u JOURNAL_STREAM -u NOTIFY_SOCKET \
  -u DOTNET_RUNNING_IN_CONTAINER -u container \
  V2RAYN_DATA_HOME="$native_data" \
  V2RAYN_WEB_AUTOSTART=true \
  ASPNETCORE_URLS="$native_url" \
  DOTNET_BUNDLE_EXTRACT_BASE_DIR="$temporary/native-bundle" \
  "$executable" --foreground --no-open >"$native_log" 2>&1 &
web_pid=$!
wait_for_health "$native_url" "$native_log"

curl --silent --show-error --fail "$native_url/api/setup/status" >"$temporary/setup-status.json"
python3 - "$temporary/setup-status.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    status = json.load(handle)
assert status["setupRequired"] is True, status
assert status["setupAllowedFromThisRequest"] is True, status
PY

setup_body="$(python3 -c 'import json,sys; print(json.dumps({"key":sys.argv[1],"confirmKey":sys.argv[1]}))' "$native_key")"
curl --silent --show-error --fail \
  -H 'Content-Type: application/json' \
  --data "$setup_body" \
  "$native_url/api/setup" >"$temporary/setup.json"
session_token="$(python3 - "$temporary/setup.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    response = json.load(handle)
assert response["setupRequired"] is False, response
print(response["token"])
PY
)"
curl --silent --show-error --fail \
  -H "Authorization: Bearer $session_token" \
  "$native_url/api/status" >"$temporary/native-status.json"
python3 - "$temporary/native-status.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    response = json.load(handle)
status = response["data"]
assert response["success"] is True, response
assert status["coreRunning"] is False, status
assert status["runtimeState"] == "stopped", status
assert not status["coreProcessIds"], status
PY
grep -Fq 'Core autostart was suppressed until first-run Management Key setup is complete.' "$native_log"
stop_instance "$native_log"

start_managed_with_key() {
  local deployment="$1" port="$2" data_home="$temporary/$1-data" output="$temporary/$1-key.log" url="http://127.0.0.1:$2"
  local key="managed-key-$(python3 -c 'import secrets; print(secrets.token_hex(24))')"
  local markers=()
  case "$deployment" in
    systemd) markers=(INVOCATION_ID=startup-security-test) ;;
    container) markers=(DOTNET_RUNNING_IN_CONTAINER=true) ;;
  esac

  env -u INVOCATION_ID -u JOURNAL_STREAM -u NOTIFY_SOCKET \
    -u DOTNET_RUNNING_IN_CONTAINER -u container \
    "${markers[@]}" V2RAYN_WEB_API_KEY="$key" \
    V2RAYN_DATA_HOME="$data_home" V2RAYN_WEB_AUTOSTART=false \
    ASPNETCORE_URLS="$url" DOTNET_BUNDLE_EXTRACT_BASE_DIR="$temporary/$deployment-bundle" \
    "$executable" --foreground --no-open >"$output" 2>&1 &
  web_pid=$!
  wait_for_health "$url" "$output"
  stop_instance "$output"
}

start_managed_with_key systemd "$(new_port)"
start_managed_with_key container "$(new_port)"

echo "Startup security tests passed (native first-run/Core suppression, managed no-key fail-closed, configured systemd/container startup)."
