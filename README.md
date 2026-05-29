# GW Detective

An offline Windows desktop app for analysing **On-premises Data Gateway**
support bundles. Drop in a `.zip` and get a navigable dashboard of errors,
query performance, Power Platform attribution, network port reachability,
and performance counters — without uploading anything anywhere.

- **Platforms:** Windows 10/11, x64 and ARM64
- **Install scope:** Per-user (`%LOCALAPPDATA%\Programs\GWDetective`) — no admin required
- **Runtime:** Self-contained .NET 8 single-file exe + Microsoft Edge WebView2
- **Privacy:** 100% local. Nothing is uploaded; parsing runs in-process.

## Features

### Ingest

- Open an On-premises Data Gateway support `.zip` via the **Open Zip File**
  button or drag-and-drop.
- Always parses everything: all logs, query reports, performance
  aggregations, and Power Platform attribution.
- Session cache: optionally reopen the most recently parsed bundle on
  next launch.
- Streaming parser handles multi-GB bundles without loading the whole
  archive into memory. Built-in safeguards still cap extreme datasets
  (180k log entries, 120k query / perf rows per category, 4k chars per
  message, 200 KB per config-file preview, 400 KB for port reports) and
  surface a notice when rows are dropped.

### Dashboard

- **Gateway identity hero card** — Name, Machine, Version, Cluster ID,
  Service Account, Region, plus ~20 expandable custom-metadata fields
  pulled from `GatewayCluster.txt` / `GatewayProperties.json`.
- Summary tiles for total events / errors / warnings / info /
  correlation IDs, plus query success rate, average duration and P90.
- Hourly stacked **timeline chart** of event volume by severity.
- **Top errors and criticals** (#1–25 by frequency) and **top error
  codes** (e.g. `80004005`) with frequency bars. Click any row to jump
  to the Errors tab with that message pre-filtered.
- **Top exception types** — .NET class names ranked by mention count.

### Logs (Errors · Info · Network · Mashup · Queries)

- Sortable, searchable tables with Timestamp · Level · Module · Message.
- The **Errors** tab replaces the Module column with a **Correlation
  Trace ID** column so failures can be jumped straight into the trace
  panel (hover shows the full list of correlated IDs).
- The **Mashup** tab parses both classic `Mashup*.log` files and the
  NDJSON container logs (`Mashup*.txt`) emitted by the gateway's mashup
  containers, promoting `Action`, `ActivityId`, `Level` and
  `ProductVersion` into the usual log shape.
- Per-tab badges show entry counts.
- The **Network** tab also injects synthesized **port-test** entries
  inline so connectivity events appear chronologically with real log
  events.

### Queries

- Query Execution Report view with success/fail tiles, total · average ·
  P90 · slowest stats, and a duration bar per row (green / orange / red
  thresholds).
- Click any query to open a trace panel with status, durations
  (read / serialization / total), data source URL, request and tracking
  IDs, and Power Platform root / current activity IDs when present.

### Power Platform

- Attribution dashboard surfaced when `QueryStartReport_*.log` is
  present in the bundle.
- Client distribution (PowerAutomate / LogicApps / Power BI Datasets /
  PowerQueryOnline / PowerApps / Dataflows), a **Sources** breakdown by
  connector type, and ranked tables of top flows / logic apps /
  connectors / endpoints by call volume.

### Performance

- Top stats strip: time range, peak CPU %, peak Gateway / Mashup memory,
  total aggregated queries, fail rate, connection status — with
  warning / danger colour thresholds.
- Six 5-minute-bucket time-series charts: CPU usage, memory usage,
  concurrent operations, thread-pool activity, queries per bucket
  (success vs fail), and average / max query duration.
- Top mashups by CPU time and slowest connection opens, with click-to-
  trace on the underlying request ID where available.

### Ports

- One card per `GatewayPorts_*.log` run, newest first.
- Pass/fail score, detected region, host name and IP, Service Bus
  namespace, proxy configuration (with a single-proxy SPOF warning),
  test timestamp + relative age, and a one-click copy-summary button.
- Per-server port grid (🟢 open · 🔴 blocked · ⚪ unknown) with live
  filter, *show failed only*, and an opt-in **group relay clusters**
  toggle that collapses `gv0…gv23` into a single roll-up row.

### Config

- Expandable list of every non-port text / config file in the bundle
  (`.config`, `.txt`, `.json`, `.xml`, …) with size and pretty-printed
  body on click. Large files are truncated with a clear indicator.

### Filtering, search and tracing

- Per-tab **Level**, **Module** and **free-text** filters with a clear
  button and live "X / Y entries" counter.
- Global **From / To datetime filter** that re-derives every tab,
  dashboard tile, chart and aggregation when changed.
- **Trace panel** (right sidebar): click any log entry with a
  correlation ID to see all correlated events as a colour-coded
  vertical timeline, or click any query to see its full metadata.
  One-click copy-trace-to-clipboard.
- **Compare ⇄** — pin any trace as **A** or **B** and open a
  side-by-side overlay showing per-side stats (event / error / warning
  counts, duration, modules, time span) and the two timelines together.

### UX

- Dark "Cool Slate" theme with semantic colours for severity.
- Virtual scrolling on large tables.
- Multi-state column sorting (asc → desc → unsorted).
- **Send Feedback** button in the top bar opens the project's GitHub
  *New Issue* form in the system browser.
- Keyboard: **Esc** closes Compare; **Ctrl+F** searches within expanded
  config previews.

### Out of scope

GW Detective is a read-only forensic viewer. There is no upload, export,
real-time tailing, multi-bundle correlation, alerting, authentication,
or report generation.

## Download

Get the latest installer from the [Releases page](https://github.com/anmateusMS/gwdetective-desktop-app/releases/latest):

| Architecture | Asset |
|---|---|
| x64   | `GWDetective-Setup-x64.exe` |
| ARM64 | `GWDetective-Setup-arm64.exe` |

The installer bundles the WebView2 Evergreen Bootstrapper and will fetch the
runtime on first install if it isn't already present. The bootstrapper handles
its own UAC prompt; the app itself installs without elevation.

## Auto-update

The app probes `releases/latest/download/latest.json` on launch, compares the
manifest's version against its own, and offers to download + verify (SHA-256)
+ silently reinstall when a newer build is published. There is no telemetry
and no background daemon — the check runs once per launch from the foreground
process.

For local QA without publishing anything, override the manifest URL:

```pwsh
$env:GWDETECTIVE_UPDATE_URL = "http://127.0.0.1:8723/latest.json"
powershell -ExecutionPolicy Bypass -File Code\installer\test-update-server.ps1
# in another shell:
dotnet run --project Code\GatewayTracer.Desktop.csproj
```

## Repository layout

```
Code/                          # WPF + WebView2 host
  App.xaml(.cs)                # Application entry
  MainWindow.xaml(.cs)         # WebView2 host window
  Parser.cs                    # Streaming zip log parser
  Updater.cs                   # Manifest probe + SHA-verified silent install
  web/                         # UI assets shipped inside the exe
  installer/
    GWDetective.iss            # Inno Setup 6 script (per-user, x64 + arm64)
    sync-version.ps1           # Pushes csproj <Version> into the .iss before each ISCC build
    publish-manifest.ps1       # Hashes the built setups, writes Output\latest.json
    test-update-server.ps1     # Localhost manifest server for updater QA
    build-icon.ps1             # Regenerates app.ico
    third-party/
      MicrosoftEdgeWebview2Setup.exe   # Bundled WebView2 Evergreen Bootstrapper
    Output/                    # ISCC outputs (git-ignored)
```

## Build from source

Requirements:
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`)
- Windows; building ARM64 from an x64 host is fine — the SDK cross-publishes.

Run from sources:

```pwsh
dotnet run --project Code\GatewayTracer.Desktop.csproj
```

## Cut a release

The single source of truth for the version is `<Version>` in
[Code/GatewayTracer.Desktop.csproj](Code/GatewayTracer.Desktop.csproj).
Everything else (the .iss `MyAppVersion`, the embedded exe FileVersion, the
manifest payload) is derived from it.

```pwsh
# 1. Bump <Version> in Code\GatewayTracer.Desktop.csproj, then:
cd Code

# 2. Publish single-file self-contained binaries for both arches.
dotnet publish GatewayTracer.Desktop.csproj -c Release -r win-x64   `
    --self-contained true /p:PublishSingleFile=true                 `
    /p:IncludeNativeLibrariesForSelfExtract=true                    `
    /p:EnableCompressionInSingleFile=true
dotnet publish GatewayTracer.Desktop.csproj -c Release -r win-arm64 `
    --self-contained true /p:PublishSingleFile=true                 `
    /p:IncludeNativeLibrariesForSelfExtract=true                    `
    /p:EnableCompressionInSingleFile=true

# 3. Push the version into the .iss, then build both installers.
cd installer
powershell -ExecutionPolicy Bypass -File .\sync-version.ps1
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /Qp /DARCH=x64   GWDetective.iss
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /Qp /DARCH=arm64 GWDetective.iss

# 4. Generate latest.json (hashes the two setup .exe files).
powershell -ExecutionPolicy Bypass -File .\publish-manifest.ps1 `
    -Version 1.0.0 `
    -BaseUrl 'https://github.com/anmateusMS/gwdetective-desktop-app/releases/download/v1.0.0' `
    -Notes   'Release notes shown in the in-app updater.'

# 5. Publish via gh CLI (or upload through the GitHub UI).
gh release create v1.0.0 `
    Output\GWDetective-Setup-x64.exe   `
    Output\GWDetective-Setup-arm64.exe `
    Output\latest.json                 `
    --title v1.0.0 --notes-file ..\..\CHANGELOG.md --latest
```

The release must be marked **Latest** for the
`releases/latest/download/latest.json` alias used by the in-app updater to
resolve to it.

## Updater contract

`Updater.ProbeAsync` returns one of four states, JSON-shaped so the renderer
patch can render them directly:

| `state`           | Meaning                                                                 |
|-------------------|-------------------------------------------------------------------------|
| `uptodate`        | Manifest parsed; remote version ≤ local                                  |
| `available`       | Newer build available for this architecture — payload `url` + `sha256`   |
| `nobuildforarch`  | Newer version exists but no asset published for the running architecture |
| `manifesterror`   | Network / parse failure — surfaced as `error`                            |

`Updater.DownloadAndLaunchAsync` streams the installer to `%TEMP%`, verifies
SHA-256 (hard fail + delete on mismatch), and runs it with
`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LAUNCHAPP`. The `/LAUNCHAPP`
switch is a hook implemented in `[Code]` inside `GWDetective.iss` that
silently relaunches the freshly installed exe.

## License

See repository for licensing terms. The bundled UI and parser are
MIT-licensed (see `// SPDX-License-Identifier: MIT` headers).
