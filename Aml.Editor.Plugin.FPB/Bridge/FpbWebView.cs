using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>
/// Lifecycle + messaging wrapper around a WebView2 control hosting FPB.JS.
/// The actual UI control is owned by the XAML; this class wires up the bridge.
/// </summary>
public sealed class FpbWebView : IDisposable
{
    private const string VirtualHost = "fpbjs.local";
    private readonly WebView2 _view;

    // Lifecycle flags (P7 — set _ready only on the JS 'ready' message, not on Navigate()):
    private bool _navStarted;     // true once Navigate() has been issued
    private bool _navSucceeded;   // true on a successful NavigationCompleted
    private bool _ready;          // true on JS 'ready' message — only then is ImportJson safe
    private string? _pendingJson;
    private string? _pendingTheme;

    private EventHandler<CoreWebView2WebMessageReceivedEventArgs>? _webMessageHandler;
    private EventHandler<CoreWebView2NavigationCompletedEventArgs>? _navigationHandler;
    private EventHandler<CoreWebView2NavigationStartingEventArgs>? _navigationStartingHandler;
    private EventHandler<CoreWebView2ProcessFailedEventArgs>? _processFailedHandler;
    private bool _disposed;

    public event Action? Ready;
    public event Action<string>? OnError;
    public event Action<string>? OnInfo;
    public event Action<JsonElement>? OnDiagramChanged;
    public event Action<string?>?    OnProcessSwitched;
    public event Action<string,string>? OnJsLog;   // (level, message) — for diagnostics

    public FpbWebView(WebView2 view)
    {
        _view = view;
    }

    /// <summary>Asynchronously initialise the WebView2 control and navigate to the bundled index.html.</summary>
    public async Task InitAsync()
    {
        if (_navStarted || _disposed) return;

        try { await _view.EnsureCoreWebView2Async(); }
        catch (Exception ex)
        {
            ReportError("WebView2 runtime missing or failed to start: " + ex.Message);
            return;
        }

        // P0-3: the await above can complete after Unloaded has torn us down. Touching
        // CoreWebView2.Settings on a disposed control throws ObjectDisposedException.
        if (_disposed) return;

        var settings = _view.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsStatusBarEnabled = false;

        var assetsPath = ResolveAssetsPath();
        OnInfo?.Invoke($"Assets path: {assetsPath}");

        if (!Directory.Exists(assetsPath))
        {
            ReportError($"fpbjs-assets folder not found at: {assetsPath}");
            return;
        }
        if (!File.Exists(Path.Combine(assetsPath, "index.html")))
        {
            ReportError($"index.html missing in assets folder: {assetsPath}");
            return;
        }
        if (!File.Exists(Path.Combine(assetsPath, "fpbjs.esm.js")))
        {
            ReportError("fpbjs.esm.js missing — was FPB.JS dist/ built before packing?");
            return;
        }

        _view.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost, assetsPath, CoreWebView2HostResourceAccessKind.Allow);

        _webMessageHandler = (_, e) => OnWebMessage(e);
        _navigationHandler = (_, ev) =>
        {
            if (ev.IsSuccess)
            {
                _navSucceeded = true;
            }
            else
            {
                _navSucceeded = false;
                _ready = false;
                ReportError($"Navigation failed: WebErrorStatus={ev.WebErrorStatus}");
            }
        };
        // A fresh navigation (initial load, or a user-triggered reload via
        // context menu / F5) tears down the JS-side message listener. Until the
        // new page re-announces 'ready', any PostWebMessage would be dropped, so
        // flip _ready off here and let ImportJson/SendTheme buffer instead.
        _navigationStartingHandler = (_, __) => _ready = false;
        // Without this, a WebView2 renderer-process crash leaves the tab blank
        // with _ready still true — every subsequent push vanishes silently. Log
        // it, drop out of the ready state so pushes buffer, and surface a banner.
        _processFailedHandler = (_, ev) =>
        {
            _ready = false;
            ReportError($"WebView2 process failed ({ev.ProcessFailedKind}). The viewer needs a Refresh from AML to recover.");
        };
        if (_disposed) return;
        _view.CoreWebView2.WebMessageReceived += _webMessageHandler;
        _view.CoreWebView2.NavigationCompleted += _navigationHandler;
        _view.CoreWebView2.NavigationStarting += _navigationStartingHandler;
        _view.CoreWebView2.ProcessFailed += _processFailedHandler;

        _navStarted = true;
        _view.CoreWebView2.Navigate($"https://{VirtualHost}/index.html");
    }

    /// <summary>
    /// Locate the plugin's fpbjs-assets/ folder. Under EnableDynamicLoading the
    /// usual <see cref="Assembly.Location"/> is reliable.
    /// </summary>
    private static string ResolveAssetsPath()
    {
        var asmLoc = typeof(FpbWebView).Assembly.Location;
        var pluginDir = !string.IsNullOrEmpty(asmLoc) ? Path.GetDirectoryName(asmLoc) : null;
        return Path.Combine(pluginDir ?? AppContext.BaseDirectory, "fpbjs-assets");
    }

    /// <summary>
    /// Push an FPB.JS JSON payload (raw text) into the modeler. Returns true when
    /// the payload was actually posted to the live page, false when it was only
    /// buffered (bridge not ready) or rejected (invalid JSON / disposed). Callers
    /// use the return value to decide whether to arm echo-suppression — a buffered
    /// push must NOT open an echo window, because the real post happens later.
    /// </summary>
    public bool ImportJson(string json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        if (_disposed) return false;

        // P7: buffer until the JS side has really told us 'ready'. Until then any
        // PostWebMessage call would be silently dropped (no listeners attached yet).
        if (!_ready || _view.CoreWebView2 == null)
        {
            _pendingJson = json;
            return false;
        }

        string envelope;
        try
        {
            using var doc = JsonDocument.Parse(json);
            envelope = JsonSerializer.Serialize(new
            {
                type = JsMessageType.ImportJSON,
                data = doc.RootElement
            });
        }
        catch (JsonException ex)
        {
            ReportError("Cannot push JSON to FPB.JS — Mapper output is not valid JSON: " + ex.Message);
            return false;
        }
        _view.CoreWebView2.PostWebMessageAsJson(envelope);
        return true;
    }

    /// <summary>
    /// P11: forward an editor theme change ("light" / "dark") to the FPB.JS viewer
    /// so the diagram + side panels follow the host's appearance.
    /// </summary>
    public void SendTheme(string theme)
    {
        if (string.IsNullOrEmpty(theme)) return;
        if (_disposed) return;
        if (!_ready || _view.CoreWebView2 == null)
        {
            _pendingTheme = theme;
            return;
        }

        var envelope = JsonSerializer.Serialize(new { type = JsMessageType.SetTheme, theme });
        _view.CoreWebView2.PostWebMessageAsJson(envelope);
    }

    /// <summary>
    /// Ask the viewer to select an element by id and scroll it into view. Used
    /// by host panels (findings DataGrid, AML tree integration, …). Silently
    /// no-ops if the bridge isn't ready yet — a missing select is recoverable,
    /// the next user interaction will work.
    /// </summary>
    public void SelectElement(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (_disposed) return;
        if (!_ready || _view.CoreWebView2 == null) return;

        var envelope = JsonSerializer.Serialize(SelectElementMessage.For(id));
        _view.CoreWebView2.PostWebMessageAsJson(envelope);
    }

    /// <summary>
    /// Fired when JS posts an <c>imported</c> acknowledgement. The payload (when
    /// present) is the viewer's OWN export snapshot of the just-imported model —
    /// the host stores it as the echo baseline: any later <c>changed</c> payload
    /// identical to it is import fallout, everything else is a real user edit.
    /// Null when the JS asset predates the baseline handshake.
    /// </summary>
    public event Action<string?>? OnImported;

    // ─── Internal: handle JS → host messages ─────────────────────────────────────
    private void OnWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed) return;
        JsToHostMessage? msg;
        try { msg = JsonSerializer.Deserialize<JsToHostMessage>(e.WebMessageAsJson); }
        catch (Exception ex) { ReportError("Failed to parse JS message: " + ex.Message); return; }
        if (msg is null) return;

        switch (msg.Type)
        {
            case JsMessageType.Ready:
                _ready = true;
                if (!string.IsNullOrEmpty(msg.Url)) OnInfo?.Invoke("FPB.JS booted at " + msg.Url);
                Ready?.Invoke();
                FlushPending();
                break;

            case JsMessageType.Changed:
                if (msg.Data.ValueKind == JsonValueKind.Undefined
                    || msg.Data.ValueKind == JsonValueKind.Null)
                    break;   // P0-5: defend the host's GetRawText() call
                OnDiagramChanged?.Invoke(msg.Data);
                break;

            case JsMessageType.ProcessSwitched:
                OnProcessSwitched?.Invoke(msg.Id);
                break;

            case JsMessageType.Error:
                ReportError(msg.Message ?? "(unspecified)");
                break;

            case JsMessageType.Imported:
                // P0-2/P0-8: the ack carries the viewer's own export snapshot —
                // the host's content-based echo baseline. Older cached assets
                // send no payload; pass null so the host can fall back.
                var baseline = msg.Data.ValueKind == JsonValueKind.Undefined
                            || msg.Data.ValueKind == JsonValueKind.Null
                    ? null
                    : msg.Data.GetRawText();
                OnImported?.Invoke(baseline);
                break;

            case JsMessageType.Log:
                OnJsLog?.Invoke(msg.Level ?? "log", msg.Message ?? "");
                break;

            default:
                ReportError($"Unknown JS message type: '{msg.Type}'.");
                break;
        }
    }

    private void FlushPending()
    {
        if (_pendingJson is { } pj)  { _pendingJson  = null; ImportJson(pj); }
        if (_pendingTheme is { } pt) { _pendingTheme = null; SendTheme(pt); }
    }

    private void ReportError(string message)
    {
        if (_view.Dispatcher.CheckAccess()) OnError?.Invoke(message);
        else _view.Dispatcher.Invoke(() => OnError?.Invoke(message));
    }

    // ─── P10: clean teardown so plugin reload doesn't leak event subscriptions ───
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            var core = _view?.CoreWebView2;
            if (core != null)
            {
                if (_webMessageHandler != null) core.WebMessageReceived -= _webMessageHandler;
                if (_navigationHandler != null) core.NavigationCompleted -= _navigationHandler;
                if (_navigationStartingHandler != null) core.NavigationStarting -= _navigationStartingHandler;
                if (_processFailedHandler != null) core.ProcessFailed -= _processFailedHandler;
            }
        }
        catch { /* CoreWebView2 may already be gone; nothing to do */ }

        _webMessageHandler = null;
        _navigationHandler = null;
        _navigationStartingHandler = null;
        _processFailedHandler = null;
        _ready = false;
        _navSucceeded = false;
        _pendingJson = null;
        _pendingTheme = null;
    }
}
