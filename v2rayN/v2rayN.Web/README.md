# v2rayN Headless Web frontend

`v2rayN.Web` is a native ASP.NET Core frontend for the existing `ServiceLib`. It runs without WPF, Avalonia, a desktop session, system-proxy integration, TUN, or elevated network capabilities. It uses the same backend and data-path behavior whether launched from a shell, systemd, Docker, or rootless Podman.

The Vue source is in `WebUI/`. The current visual draft is frozen pending confirmation of the revised layout. The replacement will use TypeScript and `vue-i18n` locale JSON files; the Backend already returns stable field names, enum codes, and `messageKey` values.

## Native Linux binary

Build on a machine or build environment that already has the .NET 10 SDK and Node.js/npm available:

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

Open `http://127.0.0.1:5080` and enter the configured API key. The mixed HTTP/SOCKS proxy listener follows the saved ServiceLib configuration (new installations keep v2rayN's `10808` default); Core autostart remains off until configured. ASP.NET Core handles `SIGINT` and `SIGTERM`; the hosted ServiceLib adapter stops Core and flushes ServiceLib profile/statistics data on shutdown.

### Data and Core paths

- By default, ServiceLib uses the executable directory when writable. Its normal `LocalApplicationData` fallback remains available when that directory is read-only.
- Set `V2RAYN_DATA_HOME=/path/to/data` to use a shared XDG data home. ServiceLib stores v2rayN state under `$V2RAYN_DATA_HOME/v2rayN` (`guiConfigs`, `guiLogs`, `bin`, and related folders).
- A self-contained single-file executable started from a read-only install directory needs `DOTNET_BUNDLE_EXTRACT_BASE_DIR` set to a writable path (the systemd and container examples use a subdirectory of the data home). A normal user-owned, writable native publish directory can run `./v2rayN.Web` directly.
- A bundled `bin/` folder beside the executable is copied into the writable ServiceLib data directory when the XDG override is active. Xray can also be checked/updated through `GET /api/core/xray/check-update` and `POST /api/core/xray/update`.
- The Core listener is controlled by the existing `Config.Inbound`; a new installation retains v2rayN's mixed HTTP/SOCKS port `10808`, loopback-only. Set `V2RAYN_WEB_PROXY_PORT` to explicitly override the saved/default port at startup, and `V2RAYN_WEB_PROXY_LISTEN_ALL=true` only when the listener should accept non-loopback clients. For an isolated local test alongside a v2rayN instance using 10808, set `V2RAYN_WEB_PROXY_PORT=1145` and use a separate `V2RAYN_DATA_HOME`.
- The WebUI/API defaults to `http://127.0.0.1:5080`. `ASPNETCORE_URLS` follows standard ASP.NET Core configuration for listen addresses and ports; set it to `http://0.0.0.0:5080` for remote access. `V2RAYN_WEB_AUTOSTART` controls starting the selected profile at process launch.

The Backend requires `V2RAYN_WEB_API_KEY`; it does not require any desktop components or Docker socket access.

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

Application/Core messages go to stdout/stderr for journald and are also retained in the ServiceLib log path when file logging is enabled.

## Optional Docker / rootless Podman

The container is a packaging option for the same self-contained publish output; it does not select a separate runtime mode or add privileged/TUN capabilities. From the repository root:

```bash
export V2RAYN_WEB_API_KEY='<strong-secret>'
docker compose -f v2rayN/v2rayN.Web/compose.yaml up -d --build
# Rootless Podman:
podman compose -f v2rayN/v2rayN.Web/compose.yaml up -d --build
```

The Compose example binds Web/API `5080` and proxy `10808` to host loopback by default and stores ServiceLib state in a named `/data` volume. Set `V2RAYN_WEB_PORT` or `V2RAYN_PROXY_PORT` to change published/container ports; for local testing beside a v2rayN instance on 10808, set `V2RAYN_PROXY_PORT=1145`. Both listeners are loopback-only from the host by default. No runtime Docker/Podman API or socket is used by the app.

## API and feature map

See [`FEATURE-MAP.md`](FEATURE-MAP.md) for the original WPF/Avalonia feature → ServiceLib → Backend API → planned Web page mapping, including the deliberately excluded desktop/system-proxy/TUN-only features and the low-fidelity desktop/mobile layouts.
