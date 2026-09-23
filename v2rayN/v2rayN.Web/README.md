# v2rayN Headless Web frontend

`v2rayN.Web` is an ASP.NET Core frontend for the existing `ServiceLib`. It runs without WPF, Avalonia, a desktop session, or system-proxy integration. TUN settings and toggling are available through the Web API/UI; activation depends on host access to `/dev/net/tun` and effective `CAP_NET_ADMIN`. Rootless containers normally lack these capabilities, and the Backend reports TUN as unavailable rather than removing the feature.

The Vue source is in `WebUI/`. The frontend uses TypeScript and `vue-i18n` locale JSON files, and calls the existing Backend/ServiceLib for profile and settings operations. Its primary workspace is a compact, high-density node table.

## Native Linux binary

Build with Node.js/npm and the .NET 10 SDK. `scripts/publish-native.sh` prefers the local, git-ignored `.NET/dotnet` SDK when present, and otherwise uses `dotnet` from `PATH`:

```bash
bash scripts/publish-native.sh linux-x64
# or: bash scripts/publish-native.sh linux-arm64
```

NuGet packages are restored into the repository's `.packages/nuget`; npm packages stay in `WebUI/node_modules` and the npm cache defaults to `.packages/npm-cache`. The publish output is self-contained and includes the built `wwwroot` frontend:

```bash
cd publish/linux-x64
V2RAYN_WEB_API_KEY='<strong-secret>' \
./v2rayN.Web
```

The Web test project is kept within this project at `Tests/v2rayN.Web.Tests.csproj`:

```bash
DOTNET="${DOTNET:-}"
if [[ -z "$DOTNET" ]]; then
  if [[ -x .NET/dotnet ]]; then DOTNET=.NET/dotnet; else DOTNET="$(command -v dotnet)"; fi
fi
"$DOTNET" test Tests/v2rayN.Web.Tests.csproj --configuration Release
```

Open `http://127.0.0.1:5080` and enter the configured API key. The mixed HTTP/SOCKS proxy listener follows the saved ServiceLib configuration (new installations keep v2rayN's `10808` default); Core autostart remains off until configured. ASP.NET Core handles `SIGINT` and `SIGTERM`; the hosted ServiceLib adapter stops Core and flushes ServiceLib profile/statistics data on shutdown.

A fresh Web publish contains the management layer but no proxy Core executable. From `v2rayN/v2rayN.Web/`, validate an official Xray release and copy it beside the native publish before enabling Core autostart:

```bash
bash scripts/bootstrap-xray.sh /path/to/Xray-linux-64.zip
mkdir -p publish/linux-x64/bin
cp -a core-bin/xray publish/linux-x64/bin/
```

### Data and Core paths

- By default, ServiceLib uses the executable directory when writable. Its normal `LocalApplicationData` fallback remains available when that directory is read-only.
- Set `V2RAYN_DATA_HOME=/path/to/data` to use a shared XDG data home. ServiceLib stores v2rayN state under `$V2RAYN_DATA_HOME/v2rayN` (`guiConfigs`, `guiLogs`, `bin`, and related folders).
- A self-contained single-file executable started from a read-only install directory needs `DOTNET_BUNDLE_EXTRACT_BASE_DIR` set to a writable path (the systemd and container examples use a subdirectory of the data home). A normal user-owned, writable native publish directory can run `./v2rayN.Web` directly.
- A bundled `bin/` folder beside the executable is copied into the writable ServiceLib data directory when the XDG override is active. Xray can also be checked/updated through `GET /api/core/xray/check-update` and `POST /api/core/xray/update`.
- The Core listener is controlled by the existing `Config.Inbound`; a new installation retains v2rayN's mixed HTTP/SOCKS port `10808`, loopback-only. Set `V2RAYN_WEB_PROXY_PORT` to explicitly override the saved/default port at startup, and `V2RAYN_WEB_PROXY_LISTEN_ALL=true` only when the listener should accept non-loopback clients. For an isolated local test alongside a v2rayN instance using 10808, set `V2RAYN_WEB_PROXY_PORT=1145` and use a separate `V2RAYN_DATA_HOME`.
- The WebUI/API defaults to `http://127.0.0.1:5080`. `ASPNETCORE_URLS` follows standard ASP.NET Core configuration for listen addresses and ports; set it to `http://0.0.0.0:5080` for remote access. `V2RAYN_WEB_AUTOSTART` controls starting the selected profile at process launch.

For remote access, prefer a trusted LAN/VPN or place v2rayN.Web behind a TLS-enabled reverse proxy. Do not expose the plain HTTP management endpoint directly to the public Internet.

The Backend requires `V2RAYN_WEB_API_KEY`; it does not require any desktop components or Docker socket access.

REST requests use a Bearer authorization header. Only `/api/events` accepts `access_token` in the query string, which is required by the browser's native `EventSource` API.

Backup restore accepts archives up to 64 MiB compressed and 256 MiB expanded, with a 2,048-entry limit and path/symlink validation. Temporary files created for backup downloads are removed after the response completes.

## Native systemd service

The example unit is `deploy/systemd/v2rayn-web.service`. It runs the same published executable as a non-root service user, stores ServiceLib state in a single `StateDirectory`, and uses the normal ASP.NET Core graceful shutdown path.

Example installation (adapt the account and paths to the server):

```bash
sudo useradd --system --home-dir /var/lib/v2rayn-web --shell /usr/sbin/nologin v2rayn
sudo install -d -o v2rayn -g v2rayn /opt/v2rayn-web
sudo cp -a publish/linux-x64/. /opt/v2rayn-web/
sudo install -m 600 deploy/systemd/v2rayn-web.env.example /etc/v2rayn-web.env
sudo install -m 644 deploy/systemd/v2rayn-web.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now v2rayn-web.service
```

Edit `/etc/v2rayn-web.env` to set a unique `V2RAYN_WEB_API_KEY`. The unit uses `V2RAYN_DATA_HOME=/var/lib/v2rayn-web`, `ASPNETCORE_URLS=http://0.0.0.0:5080`, and `Restart=always`; the latter lets a validated backup restore restart the service after the API requests shutdown.

Check service logs with:

```bash
journalctl -u v2rayn-web.service -f
```

TUN runs Core directly as the service user; the Web host does not invoke `sudo` or request a password. To grant the systemd service TUN access, install the optional drop-in:

```bash
sudo install -D -m 644 deploy/systemd/v2rayn-web-tun.conf \
  /etc/systemd/system/v2rayn-web.service.d/10-tun.conf
sudo systemctl daemon-reload
sudo systemctl restart v2rayn-web.service
```

The backend checks the capability and device access, and `/api/status` reports whether the expected TUN interface was actually created. Run `bash scripts/smoke-test-tun.sh` after selecting a working profile; it temporarily disables the optional legacy-protection pre-Core to test the selected Core's TUN interface directly, then restores both TUN preferences and the Core running state.

Application/Core messages go to stdout/stderr for journald and are also retained in the ServiceLib log path when file logging is enabled.

## Optional Docker / rootless Podman

The container is a packaging option for the same self-contained publish output. The supplied rootless configuration does not grant TUN capabilities; deployments that need TUN must explicitly provide the device and required host capabilities. From the repository root:

```bash
export V2RAYN_WEB_API_KEY='<strong-secret>'
docker compose -f v2rayN/v2rayN.Web/compose.yaml up -d --build
# Rootless Podman:
podman compose -f v2rayN/v2rayN.Web/compose.yaml up -d --build
```

The Compose example binds Web/API `5080` and proxy `10808` to host loopback by default and stores ServiceLib state in a named `/data` volume. Set `V2RAYN_WEB_PORT` or `V2RAYN_PROXY_PORT` to change published/container ports; for local testing beside a v2rayN instance on 10808, set `V2RAYN_PROXY_PORT=1145`. Both listeners are loopback-only from the host by default. No runtime Docker/Podman API or socket is used by the app.

The compose file bind-mounts `./core-bin` at `/app/bin`; the image intentionally does not download a Core during build. Prepare an official Xray Linux release archive on a machine that can reach GitHub, then from `v2rayN/v2rayN.Web/` run:

```bash
bash scripts/bootstrap-xray.sh /path/to/Xray-linux-64.zip
docker compose -f compose.yaml up -d --build
```

The bootstrap helper extracts and runs `xray -version` before placing it at `core-bin/xray/xray`. For a rootful Docker deployment that needs TUN, merge the capability/device overlay:

```bash
docker compose -f compose.yaml -f compose.tun.yaml up -d --build
```

That overlay runs the service with only `NET_ADMIN` in its capability set and maps `/dev/net/tun`. Rootless Podman typically cannot provide the host TUN capability; run `bash scripts/smoke-test-tun.sh` against the deployed instance and confirm it reports a created interface before relying on it. The script requires `curl` and `jq` on the machine where it runs.

## API and feature map

See [`FEATURE-MAP.md`](FEATURE-MAP.md) for the original WPF/Avalonia feature → ServiceLib → Backend API → Web page mapping, including TUN capability behavior and the desktop-only system-proxy exclusion.
