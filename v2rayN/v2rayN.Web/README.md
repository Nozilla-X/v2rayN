# v2rayN Headless Web frontend

`v2rayN.Web` is an ASP.NET Core frontend for the existing `ServiceLib`. It runs without WPF, Avalonia, a desktop session, or system-proxy integration. **v2rayN.Web currently does not expose TUN. TUN is intentionally deferred from the initial headless Web frontend.** If a shared or restored configuration has TUN enabled, Web refuses to start that Core configuration without changing the saved setting; use the desktop frontend to disable TUN before starting Core from Web.

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
./v2rayN.Web
```

On Linux, running the native binary directly starts a detached backend, waits for its health endpoint, and opens the WebUI when a desktop session is available. On first run, set a management key in the WebUI. Use `./v2rayN.Web --foreground` for development/debugging; `--background` explicitly selects launcher mode and `--no-open` keeps it from opening a browser. The launcher uses a per-data-directory OS lock, so a second launch opens the existing instance instead of starting another one.

The repeatable verification entry point builds the locale-checked WebUI, runs both Web and ServiceLib tests, and publishes a native `linux-x64` executable:

```bash
bash scripts/verify.sh
```

The lower-level Web tests are also runnable independently with `dotnet test Tests/v2rayN.Web.Tests.csproj --configuration Release`. `scripts/verify.sh` and `scripts/publish-native.sh` set `NUGET_PACKAGES` themselves so repository-wide NuGet restore behavior remains unchanged.

Open `http://127.0.0.1:5080`. First Run setup or sign-in uses the Management Key (`V2RAYN_WEB_API_KEY` in environment-based deployments). The key is sent only in the login/setup JSON body, is never stored in browser storage, URLs, logs, or EventSource data, and a locally created key is stored only as a PBKDF2 verifier in `guiConfigs/web-auth.json`. PBKDF2 is used only for setup and Management Key login; authenticated REST and SSE requests use the Session Token. Login exchanges the Management Key for a random 256-bit Session Token; only its SHA-256 digest is stored in memory by the Backend. Sessions slide after authenticated client-request activity with a 7-day idle expiration and a 30-day absolute expiration. “Idle” means no authenticated REST request or SSE handshake, not lack of human mouse/keyboard input; WebUI status/profile polling counts as activity. Server-generated SSE heartbeats do not renew the session. Sessions are not persisted and become invalid after a Backend restart. Login attempts are rate-limited per remote IP. The mixed HTTP/SOCKS proxy listener follows the saved ServiceLib configuration (new installations keep v2rayN's `10808` default); Core autostart remains off until configured. ASP.NET Core handles `SIGINT` and `SIGTERM`; the Web runtime owns scheduling and performs ordered Core/profile/statistics/config/database cleanup under a 20-second overall shutdown budget. If operation drain or a cleanup step times out or fails, later cleanup is skipped to avoid racing active work or closing SQLite while it may still be in use. The example systemd unit allows 45 seconds as the final process-shutdown fallback.

Subscription interval scheduling is implemented in `Services/V2rayRuntime.Scheduling.cs`; the Web host does not register ServiceLib's desktop `TaskManager`. The Web runtime's `RuntimeMutationGate` serializes its shared configuration and SQLite mutations.

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

`V2RAYN_WEB_API_KEY` is optional for native first-run setup and remains supported as the Management Key for systemd/container deployments. The Backend does not require any desktop components or Docker socket access.

First-run key setup is only accepted from IPv4/IPv6 loopback when the Host is `localhost`, `127.0.0.1`, or `::1`; forwarding headers are rejected. Remote deployments must configure `V2RAYN_WEB_API_KEY` before starting the service and sign in with that Management Key.

`POST /api/auth/login` exchanges a Management Key for a Session Token. REST requests use `Authorization: Bearer <session-token>`. `/api/events` accepts that same Session Token as `access_token` because the browser's native `EventSource` API cannot set an authorization header. Sessions are revoked on logout and held in memory only; a Backend restart requires signing in again.

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

Edit `/etc/v2rayn-web.env` to set a unique `V2RAYN_WEB_API_KEY` Management Key. The unit uses `V2RAYN_DATA_HOME=/var/lib/v2rayn-web`, `ASPNETCORE_URLS=http://0.0.0.0:5080`, `--foreground`, and `Restart=always`; the latter lets a validated backup restore restart the service after the API requests shutdown.

Check service logs with:

```bash
journalctl -u v2rayn-web.service -f
```

Application/Core messages go to stdout/stderr for journald and are also retained in the ServiceLib log path when file logging is enabled.

## Optional Docker / rootless Podman

The container is a packaging option for the same self-contained publish output. Its entrypoint explicitly runs `v2rayN.Web --foreground`. It does not require additional network-device access or Linux capabilities. From the repository root:

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

## API and feature map

See [`FEATURE-MAP.md`](FEATURE-MAP.md) for the original WPF/Avalonia feature → ServiceLib → Backend API → Web page mapping, including the intentionally deferred TUN scope and the desktop-only system-proxy exclusion.
