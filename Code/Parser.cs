// SPDX-License-Identifier: MIT
// C# port of the in-page parser Web Worker (see worker-bundle in index.html).
// Streams parsed results back to the caller as small batches so the renderer
// can build up the full result object incrementally — avoiding the 2 GB-ish
// V8 isolate ceiling that kills the in-browser parser on huge zips.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GatewayTracer.Desktop;

internal static class ParserConstants
{
    public const int MaxLogEntriesTotal   = 180_000;
    public const int MaxQueryExecRows     = 120_000;
    public const int MaxQueryStartRows    = 120_000;
    public const int MaxPerfRowsPerType   = 120_000;
    public const int RawFileMaxChars      = 200_000;
    public const int PortRawFileMaxChars  = 400_000;
    public const int MaxEntryMessageChars = 4_000;
    public const int BatchSize            = 5_000; // log entries per batch
}

internal sealed record IngestStats
{
    public int DroppedLogs       { get; set; }
    public int DroppedQueryExec  { get; set; }
    public int DroppedQueryStart { get; set; }
    public int DroppedPerf       { get; set; }
}

internal sealed class ParserSink
{
    public Func<string, Task>? OnEnvelope { get; init; }
    public async Task EmitAsync(object envelope)
    {
        if (OnEnvelope is null) return;
        var json = JsonSerializer.Serialize(envelope);
        await OnEnvelope(json).ConfigureAwait(false);
    }
}

internal sealed class GatewayZipParser
{
    // Mirrors the regex set used by the JS worker.
    private static readonly Regex LineRe = new(
        @"^(\S+)\s+(Information|Warning|Error|Critical|Verbose):\s*\d+\s*:\s*(\d{4}-\d{2}-\d{2}T[\d:.]+Z)\s+([\s\S]+)$",
        RegexOptions.Compiled);
    private const string UuidPattern = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
    private static readonly Regex UuidRe    = new(UuidPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MsgCodeRe = new(@"\b([0-9A-F]{8})\b", RegexOptions.Compiled);
    private static readonly Regex ModuleRe  = new(@"\[([^\]]+)\]\s*([\s\S]*)$", RegexOptions.Compiled);
    private static readonly Regex LineSplitRe = new(@"[^\r\n]+", RegexOptions.Compiled);
    private static readonly Regex KeyValRe    = new(@"^([^=:\r\n]+?)[=:]\s*(.+)$", RegexOptions.Compiled);

    private readonly IngestStats _stats = new();
    private long _idSeq;
    private readonly HashSet<string> _modules = new(StringComparer.Ordinal);

    public async Task ParseAsync(string zipPath, ParserSink sink, CancellationToken ct)
    {
        await sink.EmitAsync(new { ev = "parse-progress", message = "Opening zip…" }).ConfigureAwait(false);

        // Per-kind counters for cap enforcement.
        int logCount = 0, queryExecCount = 0, queryStartCount = 0;
        var perfCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["counters"] = 0, ["concurrent"] = 0, ["queryAgg"] = 0,
            ["mashupEval"] = 0, ["openConn"] = 0,
        };
        double queryMaxDur = 0;

        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        // First pass: figure out which entries we'll process so we can show
        // accurate progress numbers.
        var entries = new List<ZipArchiveEntry>(zip.Entries.Count);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // dir
            var basename = entry.Name;
            if (IsQueryExecCsv(basename) || IsQueryStartCsv(basename) || IsPerfReport(basename))
            {
                entries.Add(entry); continue;
            }
            if (IsRawFile(basename)) { entries.Add(entry); continue; }
            entries.Add(entry);
        }

        int total = entries.Count;
        int done  = 0;
        var batchLogs = new Dictionary<string, List<object>>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            var name = entry.Name;
            if (done == 1 || done == total || done % 5 == 0)
            {
                await sink.EmitAsync(new
                {
                    ev = "parse-progress",
                    message = $"Parsing {name} ({done}/{total})…"
                }).ConfigureAwait(false);
            }

            string text;
            try
            {
                using var es = entry.Open();
                using var sr = new StreamReader(es, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
                text = await sr.ReadToEndAsync().ConfigureAwait(false);
            }
            catch { continue; }

            if (IsQueryExecCsv(name))
            {
                var rows = ParseQueryExecCsv(text, ParserConstants.MaxQueryExecRows - queryExecCount, out int dropped, out double maxDur);
                if (dropped > 0) _stats.DroppedQueryExec += dropped;
                if (maxDur > queryMaxDur) queryMaxDur = maxDur;
                if (rows.Count > 0)
                {
                    queryExecCount += rows.Count;
                    await sink.EmitAsync(new { ev = "parse-batch", kind = "queryExec", items = rows }).ConfigureAwait(false);
                }
                continue;
            }
            if (IsQueryStartCsv(name))
            {
                var rows = ParseQueryStartCsv(text, ParserConstants.MaxQueryStartRows - queryStartCount, out int dropped);
                if (dropped > 0) _stats.DroppedQueryStart += dropped;
                if (rows.Count > 0)
                {
                    queryStartCount += rows.Count;
                    await sink.EmitAsync(new { ev = "parse-batch", kind = "queryStart", items = rows }).ConfigureAwait(false);
                }
                continue;
            }
            if (IsPerfReport(name))
            {
                await ParsePerfReportAsync(name, text, sink, perfCounts).ConfigureAwait(false);
                continue;
            }
            if (IsRawFile(name))
            {
                var info = ExtractGwInfo(name, text);
                if (info.Count > 0)
                    await sink.EmitAsync(new { ev = "parse-batch", kind = "gwInfo", items = info }).ConfigureAwait(false);
                var clipped = CacheRawFileText(name, text);
                await sink.EmitAsync(new { ev = "parse-batch", kind = "rawFile", name, text = clipped }).ConfigureAwait(false);
                continue;
            }
            var entryTab = GetEntryTab(name);
            if (entryTab is null)
            {
                var clipped = CacheRawFileText(name, text);
                await sink.EmitAsync(new { ev = "parse-batch", kind = "rawFile", name, text = clipped }).ConfigureAwait(false);
                continue;
            }

            await ParseLogTextAsync(text, name, entryTab, batchLogs, sink, () => logCount, n => logCount = n).ConfigureAwait(false);
        }

        // Flush any remaining log batches.
        foreach (var kv in batchLogs)
        {
            if (kv.Value.Count > 0)
            {
                await sink.EmitAsync(new { ev = "parse-batch", kind = "log", tab = kv.Key, items = kv.Value }).ConfigureAwait(false);
                kv.Value.Clear();
            }
        }

        // Emit accumulated modules (Set → array).
        if (_modules.Count > 0)
            await sink.EmitAsync(new { ev = "parse-batch", kind = "modules", items = _modules.ToArray() }).ConfigureAwait(false);

        await sink.EmitAsync(new
        {
            ev = "parse-done",
            ingestStats = new
            {
                droppedLogs       = _stats.DroppedLogs,
                droppedQueryExec  = _stats.DroppedQueryExec,
                droppedQueryStart = _stats.DroppedQueryStart,
                droppedPerf       = _stats.DroppedPerf,
            },
            queryMaxDur,
        }).ConfigureAwait(false);
    }

    // ── Log text parser ─────────────────────────────────────────────────────
    private async Task ParseLogTextAsync(
        string text, string filename, string tab,
        Dictionary<string, List<object>> batchLogs, ParserSink sink,
        Func<int> getLogCount, Action<int> setLogCount)
    {
        if (!batchLogs.TryGetValue(tab, out var bucket))
        {
            bucket = new List<object>(ParserConstants.BatchSize);
            batchLogs[tab] = bucket;
        }

        // current = the entry whose message we're still accumulating
        // continuation lines into. Mirrors the JS multi-line parser.
        Dictionary<string, object?>? current = null;
        var msgBuilder = new StringBuilder();

        async Task FlushCurrentAsync()
        {
            if (current is null) return;
            // Finalise message text.
            current["message"] = TruncateMessage(msgBuilder.ToString());
            // Module bookkeeping.
            if (current.TryGetValue("module", out var mObj) && mObj is string mStr && mStr.Length > 0)
                _modules.Add(mStr);

            if (getLogCount() >= ParserConstants.MaxLogEntriesTotal)
            {
                _stats.DroppedLogs++;
            }
            else
            {
                bucket.Add(current);
                setLogCount(getLogCount() + 1);
                if (bucket.Count >= ParserConstants.BatchSize)
                {
                    await sink.EmitAsync(new { ev = "parse-batch", kind = "log", tab, items = bucket.ToArray() }).ConfigureAwait(false);
                    bucket.Clear();
                }
            }
            current = null;
            msgBuilder.Clear();
        }

        foreach (Match lm in LineSplitRe.Matches(text))
        {
            var line = lm.Value;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parsed = ParseLine(line, filename, tab);
            if (parsed is not null)
            {
                await FlushCurrentAsync().ConfigureAwait(false);
                current = parsed;
                // Initial message was stashed under __initialMessage by ParseLine.
                if (parsed.TryGetValue("__initialMessage", out var im) && im is string s)
                {
                    msgBuilder.Append(s);
                    parsed.Remove("__initialMessage");
                }
            }
            else if (current is not null)
            {
                if (msgBuilder.Length < ParserConstants.MaxEntryMessageChars)
                {
                    int room = ParserConstants.MaxEntryMessageChars - msgBuilder.Length;
                    msgBuilder.Append('\n');
                    msgBuilder.Append(line.Length <= room ? line : line.Substring(0, Math.Max(0, room)));
                }
            }
        }
        await FlushCurrentAsync().ConfigureAwait(false);
    }

    private Dictionary<string, object?>? ParseLine(string line, string filename, string tab)
    {
        var m = LineRe.Match(line);
        if (!m.Success) return null;
        var source = m.Groups[1].Value;
        var level  = m.Groups[2].Value;
        var tsStr  = m.Groups[3].Value;
        var rest   = m.Groups[4].Value;

        if (!DateTime.TryParse(tsStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _))
            return null;

        var correlationIds = UuidRe.Matches(rest)
            .Select(x => x.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string module = "";
        string initialMessage = rest.Trim();
        var modMatch = ModuleRe.Match(rest);
        if (modMatch.Success)
        {
            module = modMatch.Groups[1].Value;
            initialMessage = modMatch.Groups[2].Value.Trim();
        }
        var codeMatch = MsgCodeRe.Match(rest);
        string messageCode = codeMatch.Success ? codeMatch.Groups[1].Value : "";

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"]              = Interlocked.Increment(ref _idSeq),
            ["source"]          = source,
            ["level"]           = level,
            ["timestamp"]       = tsStr,                 // ISO string; JS converts to Date
            ["correlationIds"]  = correlationIds,
            ["module"]          = module,
            ["messageCode"]     = messageCode,
            ["__initialMessage"] = initialMessage,
            ["file"]            = filename,
            ["tab"]             = tab,
        };
    }

    private static string TruncateMessage(string msg)
    {
        if (msg.Length <= ParserConstants.MaxEntryMessageChars) return msg;
        return msg.Substring(0, ParserConstants.MaxEntryMessageChars) + "\n… (message truncated)";
    }

    // ── File classifiers (mirror worker bundle) ─────────────────────────────
    private static string? GetEntryTab(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("gatewayerrors"))  return "errors";
        if (n.Contains("gatewayinfo"))    return "info";
        if (n.Contains("gatewaynetwork")) return "network";
        if (Regex.IsMatch(n, @"^mashup\d") || n.Contains("mashup20")) return "mashup";
        if (n.Contains("on-premises_data_gateway") ||
            n.Contains("gatewayconfigurator") ||
            n.Contains("integrationruntime"))
            return "installer";
        return null;
    }
    private static bool IsQueryExecCsv(string name)  => Regex.IsMatch(name, "queryexecutionreport", RegexOptions.IgnoreCase);
    private static bool IsQueryStartCsv(string name) => Regex.IsMatch(name, "querystartreport",     RegexOptions.IgnoreCase);
    private static bool IsPerfReport(string name)    => Regex.IsMatch(
        name,
        "SystemCounterAggregationReport|ConcurrentOperationAggregationReport|QueryExecutionAggregationReport|MashupEvaluationReport|OpenConnectionReport",
        RegexOptions.IgnoreCase);
    private static bool IsRawFile(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith(".config") || n.EndsWith(".txt")
            || n.Contains("gatewayports")
            || n.Contains("mashupcontainerprofiles")
            || n.Contains("dbproviderfactory")
            || n.Contains("exportedfilenames")
            || n.Contains("querystreaming");
    }

    private static string CacheRawFileText(string name, string text)
    {
        var lower = name.ToLowerInvariant();
        int cap = lower.Contains("gatewayports") ? ParserConstants.PortRawFileMaxChars : ParserConstants.RawFileMaxChars;
        string suffix = lower.Contains("gatewayports")
            ? "\n\n… (port report truncated in memory to reduce browser usage)"
            : "\n\n… (truncated in memory to reduce browser usage)";
        return text.Length > cap ? (text.Substring(0, cap) + suffix) : text;
    }

    // ── CSV helpers ─────────────────────────────────────────────────────────
    private static List<string> ParseCsvRow(string line)
    {
        var res = new List<string>(16);
        bool inQ = false;
        var cur = new StringBuilder(64);
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQ && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else inQ = !inQ;
            }
            else if (c == ',' && !inQ) { res.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        res.Add(cur.ToString());
        return res;
    }

    private static string ExtractDataSourceUrl(string ds)
    {
        if (string.IsNullOrEmpty(ds)) return "—";
        try
        {
            using var doc = JsonDocument.Parse(ds);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                JsonElement obj;
                if (first.ValueKind == JsonValueKind.String)
                {
                    using var inner = JsonDocument.Parse(first.GetString()!);
                    obj = inner.RootElement.Clone();
                }
                else { obj = first; }
                if (TryGetStr(obj, "FullPath", out var fp))           return fp!;
                if (TryGetStr(obj, "Server", out var sv))             return TryGetStr(obj, "Database", out var db) ? $"{sv}/{db}" : sv!;
                if (TryGetStr(obj, "path", out var p))                return p!;
                if (TryGetStr(obj, "ConnectorType", out var ct))      return ct!;
                if (TryGetStr(obj, "kind", out var kd))               return kd!;
                return ds;
            }
        }
        catch { }
        var m = Regex.Match(ds, @"https?://[^\s""\\,\]]+");
        return m.Success ? m.Value : (ds.Length > 100 ? ds.Substring(0, 100) : ds);
    }

    private static string? ExtractConnectorType(string ds)
    {
        if (string.IsNullOrEmpty(ds)) return null;
        try
        {
            using var doc = JsonDocument.Parse(ds);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                JsonElement obj;
                if (first.ValueKind == JsonValueKind.String)
                {
                    using var inner = JsonDocument.Parse(first.GetString()!);
                    obj = inner.RootElement.Clone();
                }
                else { obj = first; }
                if (TryGetStr(obj, "ConnectorType", out var ct)) return ct;
                if (TryGetStr(obj, "kind", out var kd))           return kd;
            }
        }
        catch { }
        return null;
    }

    private static bool TryGetStr(JsonElement obj, string prop, out string? value)
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(prop, out var p))  return false;
        value = p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Null   => null,
            _                    => p.ToString(),
        };
        return !string.IsNullOrEmpty(value);
    }

    // ── Query Execution CSV ─────────────────────────────────────────────────
    private static List<object> ParseQueryExecCsv(string text, int room, out int dropped, out double maxDur)
    {
        dropped = 0; maxDur = 0;
        var rows = new List<object>(1024);
        if (room <= 0) { dropped = CountNonEmptyLines(text) - 1; if (dropped < 0) dropped = 0; return rows; }
        var lines = LineSplitRe.Matches(text);
        if (lines.Count == 0) return rows;
        var headers = ParseCsvRow(lines[0].Value).Select(h => h.Trim()).ToList();
        var idx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < headers.Count; i++) idx[headers[i]] = i;
        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i].Value;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (rows.Count >= room) { dropped++; continue; }
            var vals = ParseCsvRow(line);
            string endTime = SafeGet(vals, idx, "QueryExecutionEndTimeUTC").Trim();
            int dur        = SafeInt(vals, idx, "QueryExecutionDuration(ms)");
            bool ok        = string.Equals(SafeGet(vals, idx, "Success").Trim(), "Y", StringComparison.OrdinalIgnoreCase);
            if (dur > maxDur) maxDur = dur;
            rows.Add(new
            {
                _ts        = endTime,
                _endTime   = endTime,
                _dur       = dur,
                _ok        = ok,
                _src       = ExtractDataSourceUrl(SafeGet(vals, idx, "DataSource").Trim()),
                _err       = SafeGet(vals, idx, "ErrorMessage").Trim(),
                _type      = SafeGet(vals, idx, "QueryType").Trim(),
                _trackingId = SafeGet(vals, idx, "QueryTrackingId").Trim(),
                _requestId  = SafeGet(vals, idx, "RequestId").Trim(),
                _dataRead   = SafeInt(vals, idx, "DataReadingDuration(ms)"),
                _serialize  = SafeInt(vals, idx, "DataSerializationDuration(ms)"),
            });
        }
        return rows;
    }

    // ── Query Start CSV ─────────────────────────────────────────────────────
    private static List<object> ParseQueryStartCsv(string text, int room, out int dropped)
    {
        dropped = 0;
        var rows = new List<object>(1024);
        if (room <= 0) { dropped = CountNonEmptyLines(text) - 1; if (dropped < 0) dropped = 0; return rows; }
        var lines = LineSplitRe.Matches(text);
        if (lines.Count <= 1) return rows;
        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i].Value;
            if (line is null || line.Length < 50) continue;
            if (rows.Count >= room) { dropped++; continue; }
            var vals = ParseCsvRow(line);
            if (vals.Count < 8) continue;
            string ds  = vals[2];
            string ctx = vals[7];
            string? client = null, service = null, envId = null, connId = null;
            string? workflowId = null, runId = null, root = null, current = null;
            if (!string.IsNullOrEmpty(ctx) && ctx[0] == '{')
            {
                try
                {
                    using var doc = JsonDocument.Parse(ctx);
                    if (doc.RootElement.TryGetProperty("serviceTraceContexts", out var stcArr) && stcArr.ValueKind == JsonValueKind.Array && stcArr.GetArrayLength() > 0)
                    {
                        var stc0 = stcArr[0];
                        if (stc0.TryGetProperty("serviceName", out var sn) && sn.ValueKind == JsonValueKind.String) service = sn.GetString();
                        if (stc0.TryGetProperty("traceIds", out var tidsArr) && tidsArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var t in tidsArr.EnumerateArray())
                            {
                                if (t.ValueKind != JsonValueKind.Object) continue;
                                string? key = t.TryGetProperty("key", out var kEl) && kEl.ValueKind == JsonValueKind.String ? kEl.GetString() : null;
                                string? val = null;
                                if (t.TryGetProperty("value", out var vEl) && vEl.ValueKind != JsonValueKind.Null) val = vEl.ValueKind == JsonValueKind.String ? vEl.GetString() : vEl.ToString();
                                switch (key)
                                {
                                    case "Client":            client = val; break;
                                    case "EnvironmentId":     envId = val; break;
                                    case "ConnectionId":      connId = val; break;
                                    case "WorkflowId":        workflowId = val; break;
                                    case "RunId":             runId = val; break;
                                    case "RootActivityId":    root = val; break;
                                    case "CurrentActivityId": current = val; break;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            rows.Add(new
            {
                _ts             = vals[4],
                _trackingId     = vals[3],
                _requestId      = vals[1],
                _type           = vals[5],
                _src            = ExtractDataSourceUrl(ds),
                _connectorType  = ExtractConnectorType(ds),
                _client = client, _service = service, _envId = envId, _connId = connId,
                _workflowId = workflowId, _runId = runId,
                _rootActivityId = root, _currentActivityId = current,
            });
        }
        return rows;
    }

    // ── Perf reports ────────────────────────────────────────────────────────
    private async Task ParsePerfReportAsync(string name, string text, ParserSink sink, Dictionary<string, int> counts)
    {
        var n = name.ToLowerInvariant();
        string kind =
            n.Contains("systemcounter")                  ? "counters"   :
            n.Contains("concurrentoperation")            ? "concurrent" :
            n.Contains("queryexecutionaggregation")      ? "queryAgg"   :
            n.Contains("mashupevaluation")               ? "mashupEval" :
            n.Contains("openconnection")                 ? "openConn"   : "";
        if (kind.Length == 0) return;
        int room = ParserConstants.MaxPerfRowsPerType - counts[kind];

        var lines = LineSplitRe.Matches(text);
        if (lines.Count == 0) return;
        var headers = ParseCsvRow(lines[0].Value).Select(h => h.Trim()).ToList();
        var idx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < headers.Count; i++) idx[headers[i]] = i;

        var rows = new List<object>(1024);
        int dropped = 0;
        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i].Value;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (rows.Count >= room) { dropped++; continue; }
            var vals = ParseCsvRow(line);
            switch (kind)
            {
                case "counters":   rows.Add(new {
                    CounterName             = SafeGet(vals, idx, "CounterName").Trim(),
                    AggregationStartTimeUTC = SafeGet(vals, idx, "AggregationStartTimeUTC").Trim(),
                    Average                 = SafeGet(vals, idx, "Average").Trim(),
                    Max                     = SafeGet(vals, idx, "Max").Trim(),
                    Min                     = SafeGet(vals, idx, "Min").Trim(),
                }); break;
                case "concurrent": rows.Add(new {
                    CounterName             = SafeGet(vals, idx, "CounterName").Trim(),
                    AggregationStartTimeUTC = SafeGet(vals, idx, "AggregationStartTimeUTC").Trim(),
                    Max                     = SafeGet(vals, idx, "Max").Trim(),
                }); break;
                case "queryAgg":   rows.Add(new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["AggregationStartTimeUTC"]              = SafeGet(vals, idx, "AggregationStartTimeUTC").Trim(),
                    ["Success"]                              = SafeGet(vals, idx, "Success").Trim(),
                    ["Count"]                                = SafeGet(vals, idx, "Count").Trim(),
                    ["AverageQueryExecutionDuration(ms)"]    = SafeGet(vals, idx, "AverageQueryExecutionDuration(ms)").Trim(),
                    ["MaxQueryExecutionDuration(ms)"]        = SafeGet(vals, idx, "MaxQueryExecutionDuration(ms)").Trim(),
                }); break;
                case "mashupEval": rows.Add(new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["RequestId"]                  = SafeGet(vals, idx, "RequestId").Trim(),
                    ["DataSource"]                 = SafeGet(vals, idx, "DataSource").Trim(),
                    ["TotalProcessorTime(ms)"]     = SafeGet(vals, idx, "TotalProcessorTime(ms)").Trim(),
                    ["MaxWorkingSet"]              = SafeGet(vals, idx, "MaxWorkingSet").Trim(),
                    ["MaxPercentProcessorTime"]    = SafeGet(vals, idx, "MaxPercentProcessorTime").Trim(),
                }); break;
                case "openConn":   rows.Add(new {
                    OpenConnectionStartTimeUTC = SafeGet(vals, idx, "OpenConnectionStartTimeUTC").Trim(),
                    RequestId                  = SafeGet(vals, idx, "RequestId").Trim(),
                    DataSource                 = SafeGet(vals, idx, "DataSource").Trim(),
                    OpenConnectionDuration_ms  = SafeGet(vals, idx, "OpenConnectionDuration(ms)").Trim(),
                    Success                    = SafeGet(vals, idx, "Success").Trim(),
                    ErrorMessage               = SafeGet(vals, idx, "ErrorMessage").Trim(),
                }); break;
            }
        }
        if (dropped > 0) _stats.DroppedPerf += dropped;
        if (rows.Count > 0)
        {
            counts[kind] += rows.Count;
            await sink.EmitAsync(new { ev = "parse-batch", kind = "perf", perfKind = kind, items = rows }).ConfigureAwait(false);
        }
    }

    // ── GwInfo extraction (raw config / cluster files) ──────────────────────
    private static Dictionary<string, string> ExtractGwInfo(string filename, string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var n = filename.ToLowerInvariant();
        if (!n.Contains("gatewaycluster") && !n.Contains("gatewayproperties") && !n.Contains("gatewayallclusters"))
            return result;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                JsonElement obj = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                    ? doc.RootElement[0]
                    : doc.RootElement;
                FlattenJson(obj, "", result);
                return result;
            }
            catch { /* fall through to KV parsing */ }
        }

        foreach (var line in text.Split('\n'))
        {
            var m = KeyValRe.Match(line);
            if (m.Success)
                result[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
        }
        return result;
    }

    private static void FlattenJson(JsonElement el, string prefix, Dictionary<string, string> sink)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in el.EnumerateObject())
        {
            var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    continue;
                case JsonValueKind.Array:
                    int len = prop.Value.GetArrayLength();
                    if (len == 0) continue;
                    var first = prop.Value[0];
                    if (first.ValueKind == JsonValueKind.Object)
                        sink[key] = $"{len} item{(len != 1 ? "s" : "")}";
                    else
                    {
                        var joined = string.Join(", ", prop.Value.EnumerateArray().Select(v =>
                            v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()));
                        sink[key] = joined ?? "";
                    }
                    break;
                case JsonValueKind.Object:
                    FlattenJson(prop.Value, key, sink);
                    break;
                case JsonValueKind.String:
                    sink[key] = prop.Value.GetString() ?? "";
                    break;
                default:
                    sink[key] = prop.Value.ToString();
                    break;
            }
        }
    }

    // ── Small helpers ───────────────────────────────────────────────────────
    private static string SafeGet(List<string> vals, Dictionary<string, int> idx, string col)
    {
        return idx.TryGetValue(col, out var i) && i >= 0 && i < vals.Count ? vals[i] : "";
    }
    private static int SafeInt(List<string> vals, Dictionary<string, int> idx, string col)
    {
        var s = SafeGet(vals, idx, col);
        if (string.IsNullOrEmpty(s)) return 0;
        return int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
    private static int CountNonEmptyLines(string text)
    {
        int n = 0;
        foreach (Match _ in LineSplitRe.Matches(text)) n++;
        return n;
    }
}
