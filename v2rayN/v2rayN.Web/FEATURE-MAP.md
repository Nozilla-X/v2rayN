# v2rayN Web feature map

This map is based on the existing WPF and Avalonia views plus their shared `ServiceLib.ViewModels`. The Web frontend must call the Backend API; it must not keep a second copy of ServiceLib configuration, profiles, subscriptions, routing, DNS, or statistics.

The Backend project is `v2rayN/v2rayN.Web/`; the Vue source is nested at `v2rayN/v2rayN.Web/WebUI/`. No WPF, Avalonia, or other upstream business files were changed. The existing visual draft is frozen; the redesigned Vue implementation awaits confirmation of the low-fidelity layout.

REST responses use stable JSON field names and enum codes. Operation/error responses have `{ success, code, messageKey, data }`; `messageKey` is for the frontend's `vue-i18n` dictionaries. User-provided names, URLs, profile remarks, and log lines remain data. The backend does not translate API responses.

## Original feature → ServiceLib → Web API → Web page/action

| Original v2rayN feature / interaction | Existing ServiceLib capability | Backend API | Web page/action |
|---|---|---|---|
| Main window: Servers, Subscription, Setting, Help, Reload; resizable profile pane and message/Clash tabs | `MainWindowViewModel`; `ProfilesViewModel`; `MsgViewModel`; `StatusBarViewModel`; `ClashProxiesViewModel`; `ClashConnectionsViewModel` | `GET /api/status`, `GET /api/operations`, feature endpoints below | Sidebar and compact top status; navigation sections; the profile table remains the primary workspace |
| Subscription-group chips and current group | `AppManager.SubItems`; `Config.SubIndexId`; `ProfilesViewModel.RefreshSubscriptions` | `GET /api/profile-groups`; `PUT /api/profile-groups/current` | Group chip/selector above the table |
| Profile list and original columns: type, active mark, remarks, address, port, transport, TLS, subscription, delay, speed, IP info, today/total upload and download | `AppManager.ProfileModels/ProfileItems`; `ProfileExManager`; `StatisticsManager`; `ProfileItemModel` | `GET /api/profiles?subscriptionId=&filter=` | Dense desktop table with original fields; responsive compact rows on mobile |
| Search/filter profiles | `ProfilesViewModel.ServerFilterChanged`; `Utils.IsRegexMatch` | `GET /api/profiles?filter=` uses ServiceLib's regex guard | Table filter; retain regex behavior |
| Set current profile (Enter, context menu, tray quick-select); double-click behavior | `ConfigHandler.SetDefaultServerIndex`; `CoreConfigContextBuilder.BuildAll`; `CoreManager.LoadCore` | `POST /api/profiles/{id}/select`; `POST /api/core/start|stop|restart` | Row activation/context menu; current profile remains visibly marked |
| Add/edit protocol profiles and custom group profiles | `ConfigHandler.AddServer/AddServerCommon`; protocol-specific `Add*Server` methods; `ProfileItem.IsValid`; `GroupProfileManager` | `GET /api/profiles/{id}`; `POST /api/profiles`; `PUT /api/profiles/{id}` | Profile editor using the original protocol names and fields; group profile editor preserves child ordering, subscription source/filter, and core type |
| Import from clipboard, URI text, custom config | `ConfigHandler.AddBatchServers`; `FmtHandler.ResolveConfig`; custom-config parsers | `POST /api/profiles/import` | Browser clipboard/file input submits text; no desktop clipboard or file dialog dependency |
| Remove/copy selected profiles | `ConfigHandler.RemoveServers/CopyServer` | `DELETE /api/profiles`; `POST /api/profiles/copy` | Multi-select table context actions |
| Remove duplicate profiles; remove profiles with failed tests | `ConfigHandler.DedupServerList/RemoveInvalidServerResult` | `POST /api/profiles/deduplicate`; `DELETE /api/profiles/invalid-test-results` | Table context actions |
| Move selected nodes between groups; reorder top/up/down/bottom/position | `ConfigHandler.MoveToGroup/MoveServer`; `ProfileExManager.SetSort` | `POST /api/profiles/move-to-group`; `POST /api/profiles/move` | Table context menu and keyboard actions |
| Sort by table columns, including delay/speed and traffic | `ConfigHandler.SortServers`; `EServerColName` | `POST /api/profiles/sort` | Clickable column headers |
| TCPing, real ping, UDP test, speed test, mixed test, fast real ping; cancel current batch | `SpeedtestService.RunLoop/ExitLoop`; `ESpeedActionType` | `POST /api/profiles/{id}/latency`; `POST/DELETE /api/speedtests` | Row and multi-selection test actions; progress/results via SSE |
| Share selected server; export client config/file or JSON to clipboard; raw/Base64 share URI; inner URI | `FmtHandler.GetShareUri`; `InnerFmt.ToUri`; `CoreConfigContextBuilder.Build`; `CoreConfigHandler.GenerateClientConfig` | `POST /api/profiles/export` | Context actions; browser copy/download uses returned data |
| Generate all-node or regional groups | `ConfigHandler.AddGroupAllServer/AddGroupRegionServer` | `POST /api/profile-groups/generate/all`; `POST /api/profile-groups/generate/regions` | Group actions, preserving original grouping semantics |
| Subscription table: name, URL, enabled, interval, user agent, sort; add/edit/delete/share | `SubItem`; `ConfigHandler.AddSubItem/DeleteSubItem`; `SubSettingViewModel`; `WebDav` is separate | `GET/POST/PUT/DELETE /api/subscriptions`; `GET /api/subscriptions/{id}/share` | Subscription section with original editable fields and destructive confirmation |
| Update all, update all via proxy, update selected group, update selected group via proxy | `SubscriptionHandler.UpdateProcess`; `TaskManager.UpdateTaskRunSubscription` | `POST /api/subscriptions/update`; request may specify group and proxy mode; `GET /api/operations` | Subscription toolbar actions; task progress via SSE |
| Per-subscription automatic update interval | `TaskManager.RegUpdateTask`; `SubItem.AutoUpdateInterval/UpdateTime` | Existing field exposed by subscription API; backend registers ServiceLib `TaskManager` | Per-subscription editor; scheduled updates are performed by ServiceLib |
| Core status, start/stop/restart; Xray update check/install; geodata update | `CoreManager`; `CoreInfoManager`; `UpdateService.CheckHasUpdateOnly/CheckUpdateCore/UpdateGeoFileAll` | `GET /api/status`; `POST /api/core/start|stop|restart`; `GET /api/core/xray/check-update`; `POST /api/core/xray/update`; `POST /api/core/geo/update` | Compact status and explicit core/update actions |
| v2rayN application self-update | Desktop `CheckUpdateViewModel` downloads and launches a platform updater, then exits/restarts the desktop app | No in-container self-updater; the OCI image workflow owns Web application upgrades. Xray and geodata updates remain Backend operations. | Display image/application version and update workflow status; no dead “update desktop app” button |
| Local/SOCKS/HTTP inbound display, secondary local listener, LAN listener | `Config.Inbound`; `StatusBarViewModel.InboundDisplayStatus`; `AppManager.GetLocalPort` | `GET /api/status` reports each configured mixed listener; `GET/PUT /api/settings/inbound` | Status line and inbound settings; HTTP and SOCKS are shown as protocols on the same `mixed` listener where configured |
| Live proxy/direct traffic and per-node traffic counters | `StatisticsManager`; `ServerStatItem`; `ServerSpeedItem` | Profile table carries raw byte counters; `GET /api/status` and SSE `traffic` provide current counters/rates; `DELETE /api/statistics` clears them | Table traffic columns and compact traffic strip |
| Message view: auto-refresh, regex filter, clear; Core and subscription output | `MsgViewModel`; `AppEvents.SendMsgViewRequested`; `CoreManager` callback | `GET/DELETE /api/logs?filter=`; SSE `/api/events` (`log`, `subscription-progress`, `speedtest-result`) | Log tab/pane with regex filtering, clear, and live updates |
| Option settings: inbound, Core log/fingerprint/user-agent, mux, Hysteria, fragment, statistics, speed-test parameters, source URLs, Core-type mapping | `OptionSettingViewModel`; `Config`; `ConfigHandler.SaveConfig` | `GET /api/settings`; `PUT /api/settings/inbound|core|application|speedtest|core-types` | Settings pages grouped like the original tabs; save returns `messageKey` and `restartRequired` data |
| System proxy and TUN settings/toggles | `SysProxyHandler`; `Config.TunModeItem`; status-bar ViewModel | **Intentionally no Web API** per the original deployment constraints: no system proxy, TUN, transparent proxy, `NET_ADMIN`, or privileged mode. Backend forces TUN off; TUN-specific DNS/template fields are omitted from Web DTOs. | No Web controls are to be shown for these features |
| Routing profiles: list/add/edit/remove/default/import; domain strategies | `AppManager.RoutingItems`; `ConfigHandler.SaveRoutingItem/RemoveRoutingItem/SetDefaultRouting/InitRouting` | `GET/POST/PUT/DELETE /api/settings/routing-profiles`; `/activate`; `/import`; `PUT /api/settings/routing` | Routing section and current route indicator |
| Routing rules: import, add/edit/delete/reorder/export | `RoutingItem.RuleSet`; `RulesItem`; `ConfigHandler.AddBatchRoutingRules/MoveRoutingRule` | `GET/PUT /api/settings/routing-profiles/{id}/rules`; `/rules/import`, `/rules/move`, `/rules/{ruleId}` | Nested rule table with existing outbound/type/port/network/domain/IP/protocol/process fields |
| DNS: simple DNS and per-Core DNS profiles | `Config.SimpleDNSItem`; `DNSItem`; `ConfigHandler.SaveDNSItems/GetExternalDNSItem` | `GET/PUT /api/settings/dns/simple`; `GET/PUT /api/settings/dns/profiles` | DNS settings section; `TunDNS` is omitted from Web DTOs |
| Full Xray/sing-box config templates | `FullConfigTemplateItem`; `ConfigHandler.SaveFullConfigTemplate` | `GET /api/settings/core-templates`; `PUT /api/settings/core-templates/{coreType}` | Advanced Core template editor; `TunConfig` is omitted from Web DTOs and preserved server-side |
| Regional presets | `ConfigHandler.ApplyRegionalPreset/InitRouting` | `POST /api/settings/regional-presets/{preset}` | Explicit Default/Russia/Iran actions |
| WebDAV and backup/restore | `WebDavManager`; existing backup file format (`guiConfigs/` ZIP); `FileUtils` | `GET/PUT /api/settings/webdav`; `/api/backup/webdav/check`, `/webdav`, `/webdav/restore`; `GET /api/backup/download`; multipart `POST /api/backup/restore` | Backup/restore settings and actions; restore validates ZIP paths and requests an application restart |
| Clash Proxies / Connections tabs | `ClashProxiesViewModel`, `ClashConnectionsViewModel`, `ClashApiManager` | Not exposed in the Xray-first Web slice: those tabs are tied to the sing-box/Mihomo Clash API, not Xray | No dead/placeholder tabs; can be added with a supported Core/API later |
| Global hotkeys, tray icon/menu, window placement/theme/font, startup task, UWP/admin elevation, promotion, native dialogs/scanner | WPF/Avalonia Views and platform adapters | Not applicable to a headless Web process, or explicitly excluded by the no-desktop/no-privilege scope | No fake Web controls; browser-native import/download/copy replaces the relevant file/clipboard flows |

## First Web workspace structure (low fidelity; not the current UI)

### Desktop browser

```text
┌─ v2rayN ───────────────────────────────────────────────────────────────────┐
│ Servers | Subscriptions | Routing | DNS | Settings | Logs     Core: Xray   │
│ Current profile: [name / address]    HTTP+SOCKS :10808   [Start][Stop]     │
├───────────────────────────────────────────────────────────────────────────┤
│ [All] [group A] [group B] [ + ]  [regex filter……] [TCPing] [Mixed Test]    │
├───────────────────────────────────────────────────────────────────────────┤
│ Type | ● Remarks | Address | Port | Network | TLS | Group | Delay | Speed  │
│      |           |         |      |         |     |       |       |        │
│ ...       multi-select; original context actions remain available          │
├───────────────────────────────────────────────────────────────────────────┤
│ Logs / Traffic / Core status (tabs or resizable pane; current main-window   │
│ split behavior retained)                                                    │
└───────────────────────────────────────────────────────────────────────────┘
```

The profile list stays above the fold and is the primary surface. Existing row activation, selected-row context actions, group filter, column sort, and speed-test flows are retained. Settings pages follow the original Core / General / Routing / DNS / template groupings. No system-proxy or TUN page is exposed in this deployment.

### Mobile browser

```text
┌─ v2rayN ─────────────── Core running · mixed :10808 ┐
│ Current: profile name · address                       │
│ [Stop] [Restart]                                      │
├───────────────────────────────────────────────────────┤
│ [Search/filter…] [Group ▼] [Test ▼]                   │
│ ● profile A          VMess · 42 ms         [Switch]   │
│   address:port       group name                       │
│ ● profile B          VLESS · —            [Switch]   │
│ ...                                                   │
├───────────────────────────────────────────────────────┤
│ Profiles       Subs       Routing       More          │
└───────────────────────────────────────────────────────┘
```

Desktop and mobile use the same REST/SSE state and commands. Mobile reorganizes the table into dense selectable rows and moves secondary operations into menus; it does not introduce a second state model.

## Internationalization contract

- API keys and field names stay fixed English identifiers (`profileId`, `coreType`, `restartRequired`).
- Enums/states are stable codes, not translated labels.
- REST operation responses use `success`, `code`, `messageKey`, `data`; SSE events use stable event/type codes plus data.
- The redesigned Vue source will use TypeScript (`.ts` and `<script setup lang="ts">`) and `vue-i18n`; visible UI text will live in locale JSON files, starting with `zh-CN.json`, `zh-TW.json`, and `en-US.json`. No visible string is to be hard-coded in Vue templates/components.
