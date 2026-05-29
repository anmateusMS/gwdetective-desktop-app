// Desktop-only patch loaded after the page's main script. Activated when the
// host injects window.__gtDesktop = true (set from MainWindow.xaml.cs).
//
// Phase 1 (this file): adds a "Pick file (native)" button on the dropzone
// that asks the WPF host for a native OpenFileDialog, then fetches the file
// via its file:// URL and feeds the resulting Blob into the existing
// loadZip() pipeline. This already removes one source of in-browser memory
// pressure (no duplicate File-object handling), but the underlying Web
// Worker parser still runs in the WebView2 renderer process, so very large
// zips can still hit the same structured-clone OOM.
//
// Phase 2 (next pass): replace the in-page parser with a C#-side streaming
// zip parser. We'll reuse this same { cmd, id } bridge — the host will
// stream parsed batches back via PostWebMessageAsString and the page will
// reassemble them without ever holding the full zip in JS memory.

(function () {
  if (!window.__gtDesktop || !window.chrome || !window.chrome.webview) return;

  // ─── Desktop-only styling ────────────────────────────────────────────────
  // The landing drop-card sometimes ends up offset because the page body
  // is a vertical flex container that stretches children. Force the drop
  // zone + its card to centre horizontally and vertically inside the full
  // WebView2 viewport. Browser version is untouched because this CSS only
  // loads inside the desktop host.
  (function injectDesktopCss() {
    const style = document.createElement('style');
    style.textContent = `
      /* Centering only — do NOT force 'display' or buildUI() can't hide
         the dropzone after parse completes. */
      #dropzone {
        align-items: center !important;
        justify-content: center !important;
        width: 100vw;
        height: 100vh;
      }
      #drop-card {
        margin: 0 auto !important;
      }
    `;
    if (document.head) document.head.appendChild(style);
    else document.addEventListener('DOMContentLoaded', () => document.head.appendChild(style));
  })();

  // ─── Tiny request/response bridge ────────────────────────────────────────
  const _pending     = new Map(); // id → resolve (one-shot RPCs)
  const _streamingIds = new Set(); // ids that receive many messages (parse)
  let _seq = 0;

  window.chrome.webview.addEventListener('message', (e) => {
    let envelope;
    try { envelope = typeof e.data === 'string' ? JSON.parse(e.data) : e.data; }
    catch { return; }
    if (!envelope || envelope.id == null) return;

    // Streaming: leave the id registered and dispatch every payload to the
    // streaming handler. The handler is in charge of unregistering when the
    // sequence ends (parse-done / parse-error).
    if (_streamingIds.has(envelope.id)) {
      handleParseEvent(envelope.payload);
      const ev = envelope.payload && envelope.payload.ev;
      if (ev === 'parse-done' || ev === 'parse-error') {
        _streamingIds.delete(envelope.id);
        _pending.delete(envelope.id);
      }
      return;
    }

    // Classic one-shot RPC.
    const resolve = _pending.get(envelope.id);
    if (!resolve) return;
    _pending.delete(envelope.id);
    resolve(envelope.payload);
  });

  function callHost(cmd, extra) {
    return new Promise((resolve) => {
      const id = String(++_seq);
      _pending.set(id, resolve);
      window.chrome.webview.postMessage(JSON.stringify({ cmd, id, ...(extra || {}) }));
    });
  }

  // ─── Native file picker ──────────────────────────────────────────────────
  // NOTE: WebView2's <input type="file"> already opens a real Windows file
  // dialog, so the page's built-in "Open Zip File" button is perfectly
  // native. We expose pickAndLoad() on window for future use (eg. a menu
  // item) but do NOT inject a second button into the dropzone — that
  // breaks the card's flex centering.
  async function pickAndLoad() {
    if (typeof loadZip !== 'function') { alert('App not ready yet.'); return; }
    const r = await callHost('pick-zip');
    if (!r || !r.ok) return;
    let blob;
    try {
      const resp = await fetch(r.url);
      blob = await resp.blob();
    } catch (err) {
      alert('Failed to read file:\n' + (err && err.message || err));
      return;
    }
    let file;
    try { file = new File([blob], r.name, { type: 'application/zip' }); }
    catch { file = blob; file.name = r.name; file.size = r.size; }
    loadZip(file);
  }
  window.__gtPickAndLoad = pickAndLoad;

  // ─── Streaming parse via the C# host ─────────────────────────────────────
  // Replaces the in-page Web Worker pipeline when we have a real file path
  // (only available when the user picks a file via the native dialog).
  // Drag-and-drop and the page's built-in <input type=file> only give us a
  // File/Blob with no path, so those keep using the original loadZip.
  //
  // The bridge dispatches a sequence of events:
  //   parse-meta    { fileName, fileSize }
  //   parse-progress{ message }
  //   parse-batch   { kind: 'log'|'queryExec'|'queryStart'|'perf'|'rawFile'|'gwInfo'|'modules', ... }
  //   parse-done    { ingestStats, queryMaxDur }
  //   parse-error   { error }
  // We accumulate into a freshly-shaped result and then invoke the page's
  // existing applyParseResult() so all downstream UI code keeps working.

  const LOG_TABS = ['errors', 'info', 'network', 'mashup', 'queries'];

  function emptyResult() {
    return {
      allEntries: [],
      tabEntries: Object.fromEntries(LOG_TABS.map(t => [t, []])),
      rawFiles: {},
      gwInfo: {},
      modules: [],
      queryExecRows: [],
      queryStartRows: [],
      queryMaxDur: 0,
      perf: { counters: [], concurrent: [], queryAgg: [], mashupEval: [], openConn: [] },
      ingestStats: { droppedLogs: 0, droppedQueryExec: 0, droppedQueryStart: 0, droppedPerf: 0 },
      attributionIndex: null,
    };
  }

  function reviveTimestamps(items, field) {
    for (const r of items) {
      if (r[field] && typeof r[field] === 'string') {
        const d = new Date(r[field]);
        r[field] = isNaN(d) ? null : d;
      }
    }
  }

  function buildAttributionIndex(rows) {
    if (!rows || rows.length === 0) return null;
    const idx = new Map();
    for (const r of rows) {
      const a = {
        client: r._client, workflowId: r._workflowId, runId: r._runId,
        connId: r._connId, connectorType: r._connectorType, src: r._src,
        envId: r._envId, rootActivityId: r._rootActivityId,
        currentActivityId: r._currentActivityId,
      };
      if (r._trackingId)        idx.set(String(r._trackingId).toLowerCase(),        a);
      if (r._requestId)         idx.set(String(r._requestId).toLowerCase(),         a);
      if (r._currentActivityId) idx.set(String(r._currentActivityId).toLowerCase(), a);
      if (r._rootActivityId)    idx.set(String(r._rootActivityId).toLowerCase(),    a);
    }
    return idx;
  }

  // Active parse state. Only one parse runs at a time.
  let _activeParse = null;

  function handleParseEvent(payload) {
    if (!_activeParse || !payload) return;
    const ev = payload.ev;
    switch (ev) {
      case 'parse-meta':
        _activeParse.fileName = payload.fileName;
        _activeParse.fileSize = payload.fileSize;
        break;

      case 'parse-progress':
        if (typeof setLoading === 'function') setLoading(true, payload.message);
        break;

      case 'parse-batch': {
        const r = _activeParse.result;
        switch (payload.kind) {
          case 'log': {
            const items = payload.items || [];
            for (const e of items) {
              if (e.timestamp && typeof e.timestamp === 'string') {
                const d = new Date(e.timestamp);
                if (!isNaN(d)) e.timestamp = d;
              }
            }
            const tab = payload.tab;
            if (LOG_TABS.includes(tab)) {
              const bucket = r.tabEntries[tab];
              for (let i = 0; i < items.length; i++) {
                bucket.push(items[i]);
                r.allEntries.push(items[i]);
              }
            }
            break;
          }
          case 'queryExec':
            reviveTimestamps(payload.items, '_ts');
            for (const x of payload.items) r.queryExecRows.push(x);
            break;
          case 'queryStart':
            reviveTimestamps(payload.items, '_ts');
            for (const x of payload.items) r.queryStartRows.push(x);
            break;
          case 'perf': {
            const arr = r.perf[payload.perfKind];
            if (arr) for (const x of payload.items) arr.push(x);
            break;
          }
          case 'rawFile':
            r.rawFiles[payload.name] = payload.text;
            break;
          case 'gwInfo':
            Object.assign(r.gwInfo, payload.items);
            break;
          case 'modules':
            for (const m of payload.items) r.modules.push(m);
            break;
        }
        break;
      }

      case 'parse-done': {
        const r = _activeParse.result;
        r.ingestStats = payload.ingestStats || r.ingestStats;
        r.queryMaxDur = payload.queryMaxDur || 0;
        r.attributionIndex = buildAttributionIndex(r.queryStartRows);

        try {
          applyParseResult(r);
          if (typeof bumpFileCount === 'function') bumpFileCount();
          if (typeof saveCachedSession === 'function') {
            saveCachedSession(
              { fileName: _activeParse.fileName, fileSize: _activeParse.fileSize },
              r
            );
          }
        } catch (err) {
          alert('Error applying parse result:\n' + (err && err.message || err));
          if (typeof setLoading === 'function') setLoading(false);
        }

        const resolve = _activeParse.resolve;
        _activeParse = null;
        resolve();
        break;
      }

      case 'parse-error': {
        alert('Error loading zip:\n' + payload.error);
        if (typeof setLoading === 'function') setLoading(false);
        const resolve = _activeParse.resolve;
        _activeParse = null;
        resolve();
        break;
      }
    }
  }

  /** Asks the host to parse a zip file at the given path and stream results. */
  function parseZipPath(path) {
    if (_activeParse) {
      alert('Another parse is already running.');
      return Promise.resolve();
    }
    if (typeof setLoading === 'function') setLoading(true, 'Opening zip…');
    return new Promise((resolve) => {
      _activeParse = { result: emptyResult(), resolve };
      const id = String(++_seq);
      _pending.set(id, () => {/* events flow via dedicated handler */});
      window.chrome.webview.postMessage(JSON.stringify({ cmd: 'parse-zip', id, path }));
      // Re-route this id's messages into the streaming handler.
      _streamingIds.add(id);
    });
  }

  // Replace the basic pickAndLoad with the streaming path-based version
  // when the host is available.
  pickAndLoad = async function () {
    const r = await callHost('pick-zip');
    if (!r || !r.ok) return;
    await parseZipPath(r.path);
  };
  window.__gtPickAndLoad = pickAndLoad;

  // ─── Inline update status string ─────────────────────────────────────────
  // A tiny low-contrast pill anchored to the bottom-right corner. It's the
  // only update-related UI in the desktop app — no MessageBox, no menu.
  // States, all driven by callHost('update-probe' | 'update-install'):
  //   idle           "Check for updates"            (clickable, triggers probe)
  //   checking       "Checking…"
  //   uptodate       "v<local> — up to date"        (clickable, re-probe)
  //   available      "Update to v<remote> · Install" (clickable to install)
  //   nobuildforarch "v<remote> available (no build for this CPU)"
  //   installing     "Downloading update…"
  //   error          "Update check failed — retry"  (clickable, re-probe)
  //   installerror   "Install failed — retry"       (clickable, re-probe)
  //
  // The probe fires automatically ~5s after load; clicking re-runs it.
  (function initUpdateBar() {
    const style = document.createElement('style');
    style.textContent = `
      #__gtUpdateBar {
        position: fixed; right: 10px; bottom: 6px;
        font: 11px/1.4 system-ui, -apple-system, Segoe UI, sans-serif;
        color: rgba(220,220,220,0.45);
        background: rgba(20,20,20,0.55);
        padding: 3px 8px; border-radius: 10px;
        cursor: pointer; user-select: none;
        z-index: 99999;
        transition: color .15s, background .15s;
      }
      #__gtUpdateBar:hover { color: rgba(255,255,255,0.85); background: rgba(40,40,40,0.85); }
      #__gtUpdateBar[data-state="available"] { color: #facc15; }
      #__gtUpdateBar[data-state="error"], #__gtUpdateBar[data-state="installerror"] { color: #f87171; }
      #__gtUpdateBar[data-busy="1"] { cursor: progress; opacity: 0.75; }
    `;
    (document.head || document.documentElement).appendChild(style);

    const bar = document.createElement('div');
    bar.id = '__gtUpdateBar';
    bar.dataset.state = 'idle';
    bar.textContent = 'Check for updates';

    // Last successful probe payload — needed when the user clicks Install.
    let lastProbe = null;

    function setState(state, text, busy) {
      bar.dataset.state = state;
      bar.dataset.busy  = busy ? '1' : '0';
      bar.textContent   = text;
    }

    function render(probe) {
      lastProbe = probe;
      const v = probe.remote || probe.local || '';
      switch (probe.state) {
        case 'uptodate':
          setState('uptodate', `v${probe.local} — up to date`);
          break;
        case 'available':
          setState('available', `Update to v${probe.remote} · Install`);
          break;
        case 'nobuildforarch':
          setState('nobuildforarch', `v${probe.remote} available (no build for this CPU)`);
          break;
        case 'manifesterror':
        default:
          setState('error', 'Update check failed — retry');
          break;
      }
    }

    async function probe() {
      if (bar.dataset.busy === '1') return;
      setState(bar.dataset.state, 'Checking…', true);
      const r = await callHost('update-probe');
      if (!r) { setState('error', 'Update check failed — retry'); return; }
      render(r);
    }

    async function install() {
      if (!lastProbe || lastProbe.state !== 'available') return probe();
      setState('installing', 'Downloading update…', true);
      const r = await callHost('update-install', {
        url:    lastProbe.url,
        sha256: lastProbe.sha256,
        remote: lastProbe.remote,
      });
      if (!r || !r.ok) {
        // Surface a one-line error; full details land in DevTools console.
        if (r && r.error) console.error('[update] install failed:', r.error);
        setState('installerror', 'Install failed — retry');
      } else {
        // The host will Shutdown() momentarily; just leave a friendly tail.
        setState('installing', 'Installing…', true);
      }
    }

    bar.addEventListener('click', () => {
      if (bar.dataset.state === 'available') install(); else probe();
    });

    function attach() {
      (document.body || document.documentElement).appendChild(bar);
      // Auto-probe shortly after load. Delay keeps it off the SPA's
      // first-paint critical path.
      setTimeout(probe, 5000);
    }
    if (document.body) attach();
    else document.addEventListener('DOMContentLoaded', attach);
  })();

  // Override the page's "Open Zip File" button so it uses the native dialog
  // + C# streaming parser instead of the legacy in-browser path. The button
  // is wired as onclick="document.getElementById('file-input').click()" so
  // we intercept the synthetic click on #file-input in capture phase and
  // route to pickAndLoad() instead of letting the browser open its picker.
  // Drag-and-drop keeps its original behaviour (legacy worker path).
  function patchOpenZipButton() {
    const fileInput = document.getElementById('file-input');
    if (fileInput) {
      fileInput.addEventListener('click', (e) => {
        e.preventDefault(); e.stopImmediatePropagation();
        pickAndLoad();
      }, true);
    }
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', patchOpenZipButton);
  } else {
    patchOpenZipButton();
  }

  console.log('[gtDesktop] renderer patch active (C# streaming parser)');
})();
