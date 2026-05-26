using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace GatewayTracer.Desktop;

/// <summary>
/// Hosts the existing GW Tracer single-file SPA inside a WebView2 control.
/// Provides a tiny JS bridge so the page can open a native file dialog and
/// receive a file path / URL from the .NET side. A C#-side streaming zip
/// parser (the real fix for the Full-mode OOM) will hang off of this same
/// channel in a follow-up step.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Icon = BuildAppIcon();
        Loaded += MainWindow_Loaded;
    }

    /// <summary>
    /// Builds a multi-resolution .ico in memory containing PNG frames at
    /// every common Windows icon size (16/20/24/32/48/64/128/256). Each
    /// frame is drawn fresh at its native size so Windows can pick a
    /// pixel-aligned source for the title-bar, taskbar and Alt-Tab views
    /// without having to scale a single large bitmap (which looked blurry).
    /// </summary>
    private static ImageSource BuildAppIcon()
    {
        int[] sizes = { 256, 128, 64, 48, 32, 24, 20, 16 };
        var pngFrames = new System.Collections.Generic.List<byte[]>(sizes.Length);
        foreach (var s in sizes)
            pngFrames.Add(RenderIconPng(s));

        // Build the ICONDIR header (6 bytes) + N ICONDIRENTRY (16 bytes each)
        // + concatenated PNG payloads. See:
        // https://en.wikipedia.org/wiki/ICO_(file_format)
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write((ushort)0);                 // reserved
            bw.Write((ushort)1);                 // type = icon
            bw.Write((ushort)pngFrames.Count);   // image count

            int dataOffset = 6 + 16 * pngFrames.Count;
            for (int i = 0; i < pngFrames.Count; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));   // width  (0 = 256)
                bw.Write((byte)(s >= 256 ? 0 : s));   // height (0 = 256)
                bw.Write((byte)0);                    // palette size
                bw.Write((byte)0);                    // reserved
                bw.Write((ushort)1);                  // colour planes
                bw.Write((ushort)32);                 // bits per pixel
                bw.Write((uint)pngFrames[i].Length);  // image size
                bw.Write((uint)dataOffset);           // image offset
                dataOffset += pngFrames[i].Length;
            }
            foreach (var f in pngFrames) bw.Write(f);
        }

        ms.Position = 0;
        var dec = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        // Returning Frames[0] keeps the underlying multi-frame decoder
        // attached; WPF/Win32 then picks the best-sized frame at draw time.
        return dec.Frames[0];
    }

    /// <summary>
    /// Renders the detective magnifier-over-monitor at the given pixel
    /// size and encodes it as PNG. Mirrors the SVG inside the SPA's
    /// .drop-icon (viewBox 0..80).
    /// </summary>
    private static byte[] RenderIconPng(int pixelSize)
    {
        double scale = pixelSize / 80.0;
        var accent = (Color)ColorConverter.ConvertFromString("#facc15")!;
        var softFill = new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
        softFill.Freeze();

        Pen Stroke(double w, double opacity = 1.0)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), accent.R, accent.G, accent.B));
            b.Freeze();
            // Cap stroke width so small icons don't turn into one yellow blob.
            double sw = Math.Max(0.75, w * scale);
            var p = new Pen(b, sw)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap   = PenLineCap.Round,
                LineJoin     = PenLineJoin.Round,
            };
            p.Freeze();
            return p;
        }

        Point P(double x, double y) => new(x * scale, y * scale);

        var visual = new DrawingVisual();
        // Pixel-snap drawing for crisp edges at small sizes.
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            // Lens
            dc.DrawEllipse(softFill, Stroke(3), P(32, 32), 22 * scale, 22 * scale);
            // At very small sizes the monitor detail just smears, so drop it.
            if (pixelSize >= 32)
            {
                dc.DrawRoundedRectangle(null, Stroke(2.2), new Rect(P(22, 20), P(42, 34)), 1.5 * scale, 1.5 * scale);
                if (pixelSize >= 48)
                {
                    dc.DrawLine(Stroke(1.6, 0.75), P(25, 24), P(36, 24));
                    dc.DrawLine(Stroke(1.6, 0.75), P(25, 27), P(39, 27));
                    dc.DrawLine(Stroke(1.6, 0.75), P(25, 30), P(33, 30));
                }
                dc.DrawLine(Stroke(2.4), P(32, 34), P(32, 40));
                dc.DrawLine(Stroke(2.6), P(27, 41), P(37, 41));
            }
            if (pixelSize >= 48)
            {
                var shine = new StreamGeometry();
                using (var sgc = shine.Open())
                {
                    sgc.BeginFigure(P(18, 22), false, false);
                    sgc.QuadraticBezierTo(P(14, 28), P(16, 34), true, false);
                }
                shine.Freeze();
                dc.DrawGeometry(null, Stroke(2.5, 0.45), shine);
            }
            // Handle
            dc.DrawLine(Stroke(5), P(48, 48), P(68, 68));
        }

        var bmp = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var pngStream = new MemoryStream();
        enc.Save(pngStream);
        return pngStream.ToArray();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Keep WebView2's user-data folder beside the exe so the app stays
        // truly portable (no AppData scribbling).
        var exeDir   = AppContext.BaseDirectory;
        var userData = Path.Combine(exeDir, "WebView2UserData");
        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userData);
        await Web.EnsureCoreWebView2Async(env);

        Web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        // Expose a flag the page can sniff to switch on desktop behaviour.
        // renderer-patch.js (bundled in /web) uses it to gate the native
        // picker and the eventual C# parser hook.
        await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.__gtDesktop = true;");

        var indexPath = Path.Combine(exeDir, "web", "index.html");
        if (!File.Exists(indexPath))
        {
            MessageBox.Show(this,
                $"index.html not found at:\n{indexPath}\n\nDid the build copy /web?",
                "GW Detective", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Web.CoreWebView2.Navigate(new Uri(indexPath).AbsoluteUri);
        // The renderer patch drives the update check itself (sends an
        // "update-probe" bridge message ~5s after load and renders the
        // result inline in the page footer). No WPF UI is involved.
    }

    /// <summary>
    /// Messages posted via window.chrome.webview.postMessage. Envelope:
    /// { "cmd": "...", "id": "..." , ...payload }.
    /// </summary>
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try   { raw = e.TryGetWebMessageAsString(); }
        catch { raw = e.WebMessageAsJson; }
        if (string.IsNullOrWhiteSpace(raw)) return;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var cmd  = root.GetProperty("cmd").GetString();
            var id   = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

            switch (cmd)
            {
                case "pick-zip":
                    await HandlePickZipAsync(id);
                    break;
                case "parse-zip":
                    var path    = root.TryGetProperty("path",    out var pEl) ? pEl.GetString() : null;
                    await HandleParseZipAsync(id, path);
                    break;
                case "update-probe":
                    await HandleUpdateProbeAsync(id);
                    break;
                case "update-install":
                    var url    = root.TryGetProperty("url",    out var uEl) ? uEl.GetString()    : null;
                    var sha    = root.TryGetProperty("sha256", out var sEl) ? sEl.GetString()    : null;
                    var remote = root.TryGetProperty("remote", out var rEl) ? rEl.GetString()    : null;
                    await HandleUpdateInstallAsync(id, url, sha, remote);
                    break;
                default:
                    break; // forward-compat: unknown commands are ignored
            }
        }
        catch (Exception ex)
        {
            await Web.CoreWebView2.ExecuteScriptAsync(
                $"console.error('Bridge error:', {JsonSerializer.Serialize(ex.Message)});");
        }
    }

    private async Task HandlePickZipAsync(string? id)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Open gateway logs zip",
            Filter = "Zip archive (*.zip)|*.zip",
        };
        bool? ok = dlg.ShowDialog(this);
        if (ok != true)
        {
            await ReplyAsync(id, new { ok = false, cancelled = true });
            return;
        }

        var path = dlg.FileName;
        var name = Path.GetFileName(path);
        var size = new FileInfo(path).Length;
        var url  = new Uri(path).AbsoluteUri;

        await ReplyAsync(id, new { ok = true, name, size, url, path });
    }

    private Task ReplyAsync(string? id, object payload)
    {
        var envelope = JsonSerializer.Serialize(new { id, payload });
        Web.CoreWebView2.PostWebMessageAsString(envelope);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Probes the update manifest and returns a JSON status to the renderer.
    /// All UI for the result lives in renderer-patch.js — see
    /// renderUpdateState() there. Never throws into the bridge.
    /// </summary>
    private async Task HandleUpdateProbeAsync(string? id)
    {
        var status = await Updater.ProbeAsync(CancellationToken.None);
        await ReplyAsync(id, status);
    }

    /// <summary>
    /// Downloads the installer the renderer told us to fetch (url + sha256
    /// from the most recent probe) and, on success, shuts the app down so
    /// the silent installer can overwrite our files. Any failure is sent
    /// back as { ok: false, error } for inline rendering.
    /// </summary>
    private async Task HandleUpdateInstallAsync(string? id, string? url, string? sha256, string? remote)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha256))
        {
            await ReplyAsync(id, new Updater.InstallResult { ok = false, error = "Missing url or sha256" });
            return;
        }

        var result = await Updater.DownloadAndLaunchAsync(url, sha256, remote ?? "0.0.0.0", CancellationToken.None);
        await ReplyAsync(id, result);
        if (result.ok)
        {
            // Give the renderer a beat to paint the "installing\u2026" state
            // and the PostWebMessageAsString to flush before we tear down.
            await Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(300);
                Application.Current.Shutdown();
            });
        }
    }

    /// <summary>
    /// Streams a C#-side zip parse back to the renderer in small batches.
    /// Each batch is a pre-serialised JSON envelope so the renderer can
    /// just JSON.parse and dispatch — keeps the bridge cheap and avoids
    /// any large in-memory result object that would re-trigger the same
    /// OOM we hit in the browser.
    /// </summary>
    private async Task HandleParseZipAsync(string? id, string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            await SendParseEventAsync(id, new { ev = "parse-error", error = "File not found: " + path });
            return;
        }

        try
        {
            // Acknowledge the request immediately so the renderer can wire
            // its accumulator and show progress UI.
            await SendParseEventAsync(id, new { ev = "parse-meta", fileName = Path.GetFileName(path), fileSize = new FileInfo(path).Length });

            var parser = new GatewayZipParser();
            var sink = new ParserSink
            {
                OnEnvelope = async (string innerJson) =>
                {
                    // Marshal back to UI thread; PostWebMessageAsString is UI-affine.
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var wrapped = "{\"id\":" + JsonSerializer.Serialize(id) + ",\"payload\":" + innerJson + "}";
                        Web.CoreWebView2.PostWebMessageAsString(wrapped);
                    });
                }
            };

            // Run the heavy parse off the UI thread.
            await Task.Run(() => parser.ParseAsync(path, sink, CancellationToken.None));
        }
        catch (Exception ex)
        {
            await SendParseEventAsync(id, new { ev = "parse-error", error = ex.Message });
        }
    }

    private Task SendParseEventAsync(string? id, object payload)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            var envelope = JsonSerializer.Serialize(new { id, payload });
            Web.CoreWebView2.PostWebMessageAsString(envelope);
        }).Task;
    }
}
