using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GatewayTracer.Desktop;

/// <summary>
/// Lightweight, network-quiet auto-updater. Surfaces no UI itself — the
/// host calls <see cref="ProbeAsync"/> to discover whether an update is
/// available and then <see cref="DownloadAndLaunchAsync"/> to apply it.
/// Result objects are JSON-friendly so the renderer (WebView2 SPA) can
/// render the status inline without ever popping a WPF MessageBox.
/// </summary>
internal static class Updater
{
    // Static URL pointing at a small JSON manifest. Replace with your real
    // host (GitHub Releases "latest" download URL, an S3 object, etc.).
    // The manifest is tiny (<1 KB) and safe to fetch on every launch.
    //
    // For QA / local testing, set the GWDETECTIVE_UPDATE_URL environment
    // variable to override this without rebuilding (e.g. point at a local
    // http://localhost:.../latest.json served by a test rig).
    private const string DefaultManifestUrl =
        "https://github.com/anmateusMS/gwdetective-desktop-app/releases/latest/download/latest.json";

    public static string ManifestUrl =>
        Environment.GetEnvironmentVariable("GWDETECTIVE_UPDATE_URL") is { Length: > 0 } envUrl
            ? envUrl
            : DefaultManifestUrl;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GWDetective-Updater/1.0");
        return c;
    }

    private sealed class ArchPayload
    {
        public string? url    { get; set; }
        public string? sha256 { get; set; }
    }

    private sealed class Manifest
    {
        public string?      version { get; set; }
        public string?      notes   { get; set; }
        public ArchPayload? x64     { get; set; }
        public ArchPayload? arm64   { get; set; }
    }

    /// <summary>
    /// Result of a probe. Field names match the JSON shape the renderer
    /// patch expects (see web/renderer-patch.js — renderUpdateState()).
    /// </summary>
    public sealed class ProbeResult
    {
        // One of: uptodate | available | nobuildforarch | manifesterror
        public string  state   { get; set; } = "";
        public string  local   { get; set; } = ""; // running version, always present
        public string? remote  { get; set; }       // manifest version, if parsed
        public string? notes   { get; set; }       // release notes from manifest
        public string? error   { get; set; }       // when state == "manifesterror"
        // Carried back so the renderer can echo it into the install call
        // without us having to keep server-shaped state on the C# side.
        public string? url     { get; set; }
        public string? sha256  { get; set; }
    }

    public sealed class InstallResult
    {
        public bool    ok    { get; set; }
        public string? error { get; set; }
    }

    public static async Task<ProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var local = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        var result = new ProbeResult { local = local.ToString() };

        Manifest? manifest;
        try
        {
            using var resp = await Http.GetAsync(ManifestUrl, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            manifest = await JsonSerializer.DeserializeAsync<Manifest>(s, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Updater] manifest fetch failed: {ex.Message}");
            result.state = "manifesterror";
            result.error = ex.Message;
            return result;
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.version) ||
            !Version.TryParse(manifest.version, out var remote))
        {
            result.state = "manifesterror";
            result.error = "Manifest was empty or unparseable.";
            return result;
        }

        result.remote = remote.ToString();
        result.notes  = manifest.notes;

        if (remote <= local)
        {
            result.state = "uptodate";
            return result;
        }

        var payload = PickArchPayload(manifest);
        if (payload?.url is null || payload.sha256 is null)
        {
            result.state = "nobuildforarch";
            return result;
        }

        result.state  = "available";
        result.url    = payload.url;
        result.sha256 = payload.sha256;
        return result;
    }

    /// <summary>
    /// Downloads the installer to %TEMP%, verifies its SHA-256, runs it
    /// silently with the /LAUNCHAPP relaunch hook, and returns success.
    /// The caller is responsible for shutting the process down on ok=true
    /// so the installer can overwrite our own files.
    /// </summary>
    public static async Task<InstallResult> DownloadAndLaunchAsync(string url, string sha256, string versionString, CancellationToken ct = default)
    {
        try
        {
            var version = Version.TryParse(versionString, out var v) ? v : new Version(0, 0, 0, 0);
            var path    = await DownloadAndVerifyAsync(url, sha256, version, ct).ConfigureAwait(false);
            var psi = new ProcessStartInfo
            {
                FileName        = path,
                // /LAUNCHAPP is a custom switch handled by GWDetective.iss
                // to relaunch the new build once the silent install finishes.
                Arguments       = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LAUNCHAPP",
                UseShellExecute = true,
            };
            Process.Start(psi);
            return new InstallResult { ok = true };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Updater] install failed: {ex.Message}");
            return new InstallResult { ok = false, error = ex.Message };
        }
    }

    private static ArchPayload? PickArchPayload(Manifest m) =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => m.arm64,
            Architecture.X64   => m.x64,
            _                  => null, // x86 / unknown: no build published
        };

    private static async Task<string> DownloadAndVerifyAsync(string url, string expectedSha256Hex, Version version, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "GWDetective.Updates");
        Directory.CreateDirectory(tempDir);
        var outPath = Path.Combine(tempDir, $"GWDetective-Setup-{version}.exe");

        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var net  = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(outPath);
            await net.CopyToAsync(file, 81920, ct).ConfigureAwait(false);
        }

        // Hash verification is the only integrity check we have without
        // Authenticode — treat any mismatch as a hard failure and delete
        // the file so a later retry can't accidentally run a tampered .exe.
        string actual;
        await using (var fs = File.OpenRead(outPath))
        {
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            actual = Convert.ToHexString(hash);
        }

        if (!string.Equals(actual, expectedSha256Hex.Replace("-", "").Trim(), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(outPath); } catch { /* best-effort */ }
            throw new InvalidDataException(
                $"Installer SHA-256 mismatch.\nExpected: {expectedSha256Hex}\nActual:   {actual}");
        }

        return outPath;
    }
}
