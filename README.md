# GW Detective

Desktop edition of the GW Tracer gateway log analyser. Wraps the existing
single-file SPA in a WebView2 host and adds a C# streaming-zip parser, so
multi-gigabyte log bundles that crash the in-browser worker open cleanly.

- **Platforms:** Windows 10/11, x64 and ARM64
- **Install scope:** Per-user (`%LOCALAPPDATA%\Programs\GWDetective`) — no admin required
- **Runtime:** Self-contained .NET 8 single-file exe + Microsoft Edge WebView2

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
  Parser.cs                    # Streaming zip log parser (replaces the in-page worker)
  Updater.cs                   # Manifest probe + SHA-verified silent install
  web/                         # SPA shipped inside the exe (index.html + renderer-patch.js)
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
    -Version 0.1.0 `
    -BaseUrl 'https://github.com/anmateusMS/gwdetective-desktop-app/releases/download/v0.1.0' `
    -Notes   'Release notes shown in the in-app updater.'

# 5. Publish via gh CLI (or upload through the GitHub UI).
gh release create v0.1.0 `
    Output\GWDetective-Setup-x64.exe   `
    Output\GWDetective-Setup-arm64.exe `
    Output\latest.json                 `
    --title v0.1.0 --notes-file ..\..\CHANGELOG.md --latest
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

See repository for licensing terms. SPA bundle and parser are MIT-licensed
(see `// SPDX-License-Identifier: MIT` headers).
