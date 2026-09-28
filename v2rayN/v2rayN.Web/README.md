# v2rayN Web

`v2rayN.Web` is a headless ASP.NET Core frontend for v2rayN's existing `ServiceLib`. It runs on Linux without WPF, Avalonia, a desktop session, a Docker socket, or system-proxy integration. The Vue frontend lives in `WebUI/` and uses the Web backend for configuration, profile, subscription, routing, DNS, runtime, and backup operations.

The intended use includes native Linux servers and NAS devices: start the service on the server, then manage it from another trusted computer at `http://<server-private-ip>:5080`. The frontend is intentionally independent of the desktop projects; no existing ServiceLib, WPF, or Avalonia source is modified.

## Network and first-run security

The default Web bind remains **`http://0.0.0.0:5080`** so native NAS/headless installs are reachable from the LAN without first logging in over SSH to change the bind address. `0.0.0.0` listens on all available IPv4 interfaces. Review the host firewall and all network interfaces before deployment; global IPv6 routing or another wildcard IPv6 bind can also make a service publicly reachable, even when no IPv4 port-forward is configured.

Interactive native installs may start without `V2RAYN_WEB_API_KEY` and initialize from either loopback or a directly connected trusted private network. LAN setup is allowed only when both client and server addresses are private and the HTTP `Host` exactly matches the server's private IP. Public addresses, mismatched hosts, and requests carrying `Forwarded`, `X-Forwarded-*`, or `X-Real-IP` headers cannot initialize setup. Forwarding headers never grant setup access.

> Interactive native installs support first-run setup from loopback or a directly connected trusted private network. On shared/untrusted networks or Internet-facing deployments, configure `V2RAYN_WEB_API_KEY` before starting the service.

`0.0.0.0` means all available network interfaces. Do not expose port 5080 directly to the public Internet. Use firewall rules, a VPN, or an HTTPS reverse proxy for remote access. Protect IPv4 **and** IPv6 paths; do not assume the absence of IPv4 port forwarding makes a host private.

Systemd/supervised and container deployments require a non-empty `V2RAYN_WEB_API_KEY`; otherwise startup is refused with an actionable error. They never fall back to remote first-run setup. Configure a unique key before starting these deployments. With an environment key set, first-run setup is disabled and that key is used for login.

Before Management Key setup completes, the Web host and setup page can run, but Core autostart and runtime-recovery startup are temporarily suppressed—even if `V2RAYN_WEB_AUTOSTART=true` or a previous runtime intent requested a start. The saved autostart setting is not changed. After setup, Core can be started normally from the UI or by the existing configured behavior on a later service start.

## Authentication model

- The Management Key is exchanged only by `POST /api/auth/login`; it is not a REST or SSE bearer token and is never put in a URL or logged.
- A key created through setup is stored as a PBKDF2-HMAC-SHA256 verifier with a random salt and 600,000 iterations. Environment-provided keys are not persisted.
- Login returns a random 256-bit Session Token. REST APIs use `Authorization: Bearer <session-token>`; only its SHA-256 digest is retained in memory.
- Sessions use a 7-day sliding idle lifetime and a 30-day absolute lifetime. Logout revokes a session; sessions and SSE tickets expire on restart.
- EventSource receives a separate random, one-time ticket valid for 45 seconds. The ticket cannot authorize REST requests, and its stream closes when the owning session is revoked or expires.
- Login and setup are rate-limited by direct remote IP. Forwarding headers are not used to calculate setup eligibility or the rate-limit identity.

## Native Linux

Download and extract the full-install asset for the server architecture, then run `./v2rayN.Web`. The interactive native launcher starts a detached backend and opens a browser when a desktop is available; `./v2rayN.Web --foreground --no-open` is suitable for a terminal or a process supervisor. Use `./v2rayN.Web --stop` for an instance started by the native background launcher. `V2RAYN_DATA_HOME=/path/to/data` selects a writable ServiceLib data directory.

Native builds require Node.js/npm and the .NET 10 SDK. From this directory:

```bash
bash Scripts/publish-native.sh linux-x64
# or linux-arm64
bash Scripts/verify.sh
```

`Scripts/verify.sh` checks frontend locales, builds and tests the Vue frontend, runs Web and ServiceLib tests, publishes a native linux-x64 smoke build, and validates lightweight packaging/manifest fixtures. A local native publish contains the Web application; official install packages and containers add the complete architecture-matched upstream Linux Core bundle.

## systemd

The example unit is `Deploy/Systemd/v2rayn-web.service`. It runs the Web executable in the foreground as an unprivileged service user, stores application data under `/var/lib/v2rayn-web`, and listens on all interfaces at port 5080 by default. The environment example intentionally leaves the key empty: fill it before starting the unit. Protect the environment file with mode `600`.

Generate a key, for example, with `openssl rand -hex 32`, then set it in `/etc/v2rayn-web.env`:

```ini
V2RAYN_WEB_API_KEY=<unique-random-secret>
```

Install/update the unit and start it only after configuring the key:

```bash
sudo install -m 600 Deploy/Systemd/v2rayn-web.env.example /etc/v2rayn-web.env
sudoedit /etc/v2rayn-web.env
sudo install -m 644 Deploy/Systemd/v2rayn-web.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now v2rayn-web.service
```

For a reverse proxy on the same host, optionally set `ASPNETCORE_URLS=http://127.0.0.1:5080` in the unit and terminate TLS at the proxy. Otherwise restrict port 5080 to trusted LAN/VPN clients with firewall rules. Use `systemctl stop v2rayn-web.service` to stop a systemd-managed instance.

## Docker / Podman

The Compose example requires `V2RAYN_WEB_API_KEY` and refuses to interpolate an empty value:

```bash
export V2RAYN_WEB_API_KEY="$(openssl rand -hex 32)"
docker compose -f v2rayN/v2rayN.Web/compose.yaml up -d --build
# or: podman compose -f v2rayN/v2rayN.Web/compose.yaml up -d --build
```

The container process listens on `0.0.0.0:5080`; the example publishes the Web port on host loopback by default. For trusted-LAN access, change the host-side port mapping deliberately and keep the Management Key configured. The proxy listener and Web port can be adjusted through the Compose environment settings. The image does not require extra Linux capabilities or access to the container engine socket.

The default build target is linux-x64. For ARM64, set `V2RAYN_BUILD_RID=linux-arm64` and build for `linux/arm64`. The Containerfile downloads and embeds the complete architecture-matched upstream Core bundle.

## Release assets and self-update

Official Web packages are attached to the same version tag and GitHub Release as v2rayN:

- `v2rayN-linux-64-web.zip` and `v2rayN-linux-arm64-web.zip` — full fresh installs.
- `v2rayN-linux-64-web-update.zip` and `v2rayN-linux-arm64-web-update.zip` — app-only updates.
- `web-update.json` — version, build identity, RID, asset sizes, and SHA-256 digests.

The existing Linux release workflow builds and validates the Web artifacts, then the official signing/upload workflow adds them to that release. Asset naming follows the existing Linux release convention; there is no separate Web version/tag stream. The embedded build repository is used for self-update isolation: a build only trusts the release assets belonging to the repository that produced it.

Writable native Linux installs can apply a verified Web update transactionally. Systemd deployments are check-only and require an administrator to install the next package; containers are check-only and require a new image/container. App-only archives never replace Core binaries, user data, or the Management Key verifier.

## Intentionally unsupported

The Web frontend does not expose desktop-only system proxy controls, tray icons, global hotkeys, window integration, or native scanner/file dialogs. TUN, Clash Proxies, and Clash Connections are intentionally deferred; Web does not modify ServiceLib to approximate those desktop features. If a saved configuration enables TUN, Web refuses to start the generated Core configuration and leaves the saved setting unchanged. Browser-native file/clipboard capabilities are used where applicable.

See [`FEATURE-MAP.md`](FEATURE-MAP.md) for the detailed WPF/Avalonia → ServiceLib → Web API → frontend mapping and current feature boundaries.
