// One viewer-tab in the FPB.js plugin: renders a single InstanceHierarchy
// (one fpb:Project in CAEX terms), holds its own WebView2 + bridge + state.
//
// Lifecycle is owned by FpbPlugin which creates an IhView per FPD-bearing IH
// on DocumentLoaded and disposes them on DocumentUnLoaded.

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Aml.Editor.Plugin.FPB.Bridge;
using Aml.Editor.Plugin.FPB.Diagnostics;
using Aml.Editor.Plugin.FPB.Validation;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using Microsoft.Win32;

namespace Aml.Editor.Plugin.FPB.Views;

public partial class IhView : UserControl, IDisposable
{
    // ── Per-view state ────────────────────────────────────────────────────
    private CAEXDocument? _doc;
    private InstanceHierarchyType? _ih;
    private string _ihLabel = "";
    private PluginSettings _settings = new();
    private FpbWebView? _bridge;

    private string? _pendingSnapshot;
    private string _lastIhHash = string.Empty;
    private DateTime _lastImportFromHost = DateTime.MinValue;
    private bool _liveSyncSuppressed;
    private bool _disposed;

    private DispatcherTimer? _liveSyncTimer;
    private static readonly TimeSpan LiveSyncPollInterval = TimeSpan.FromSeconds(2);
    // Widened to 3 s after we observed initial-render 'changed' events arriving
    // ~2 s after the import on slower machines. The pre-stamp in PushIhToWebView
    // gets us past the first wave; this window covers the late settling pass.
    private static readonly TimeSpan EchoSuppressionWindow = TimeSpan.FromMilliseconds(3000);

    /// <summary>Raised when the pending-snapshot state changes (host updates the tab header badge).</summary>
    public event Action<IhView, bool>? PendingChanged;

    /// <summary>Raised when the IH's display name drifts (host updates the tab header text).</summary>
    public event Action<IhView, string>? LabelChanged;

    public bool HasPendingSnapshot => !string.IsNullOrEmpty(_pendingSnapshot);
    public string IhLabel => _ihLabel;
    public InstanceHierarchyType? Ih => _ih;

    public IhView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Bind this view to a specific InstanceHierarchy in <paramref name="doc"/> and
    /// fire the WebView2 init + initial document push.
    /// </summary>
    public async Task BindAsync(CAEXDocument doc, InstanceHierarchyType ih, string ihLabel, PluginSettings settings)
    {
        _doc = doc;
        _ih = ih;
        _ihLabel = ihLabel;
        _settings = settings;

        _bridge = new FpbWebView(WebView);
        _bridge.Ready += () =>
        {
            PluginLog.Debug($"IhView '{_ihLabel}': bridge ready.");
            WebViewPlaceholder.Visibility = Visibility.Collapsed;
            if (_doc != null && _ih != null) PushIhToWebView();
        };
        _bridge.OnInfo  += msg => PluginLog.Info($"[{_ihLabel}] {msg}");
        _bridge.OnError += msg => PluginLog.Error($"[{_ihLabel}] viewer: {msg}");
        _bridge.OnDiagramChanged += OnDiagramChangedFromJs;
        _bridge.OnImported += () =>
        {
            _lastImportFromHost = DateTime.UtcNow;
            PluginLog.Debug($"[{_ihLabel}] import acknowledged by JS.");
        };
        _bridge.OnJsLog += (lvl, msg) => PluginLog.FromJs(lvl, $"[{_ihLabel}] {msg}");

        try { await _bridge.InitAsync(); }
        catch (Exception ex) { PluginLog.Error($"[{_ihLabel}] WebView2 init failed", ex); }

        StartLiveSync();
    }

    public void SendTheme(string theme) => _bridge?.SendTheme(theme);

    /// <summary>Push this IH's content into the viewer (used after AML-side edits).</summary>
    public void PushIhToWebView()
    {
        if (_disposed || _bridge == null || _doc == null || _ih == null) return;
        try
        {
            var result = CaexToFpbJson.Convert(_doc, _ih);
            // Pre-stamp the echo window BEFORE we post the import envelope to JS —
            // diagram-js fires 'changed' asynchronously during settling, and on slow
            // machines that 'changed' can arrive at the host before the JS-side
            // 'imported' ack does. Pre-stamping closes the race.
            _lastImportFromHost = DateTime.UtcNow;
            _bridge.ImportJson(result.Value);
            foreach (var w in result.Warnings) PluginLog.Warn($"[{_ihLabel}] {w}");

            // After every successful push, check whether the IH was renamed in the
            // AML tree — if so, surface the new name so the host can update the
            // tab header text (the project name inside FPB.js already updated via
            // the fresh Convert output above).
            var currentName = _ih.Name;
            if (!string.IsNullOrWhiteSpace(currentName) && !string.Equals(currentName, _ihLabel, StringComparison.Ordinal))
            {
                _ihLabel = currentName;
                try { LabelChanged?.Invoke(this, _ihLabel); }
                catch (Exception ex) { PluginLog.Error("LabelChanged handler threw", ex); }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[{_ihLabel}] conversion failed", ex);
            SetStatus("Conversion failed: " + ex.Message);
        }
    }

    // ── Update-Button: push pending FPB.js state into the IH ──────────────
    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _ih == null) return;
        var snapshot = _pendingSnapshot;
        if (string.IsNullOrEmpty(snapshot))
        {
            SetStatus("No pending viewer changes to apply.");
            return;
        }

        // Safety check before mutating the document. If the diff looks suspicious
        // (more adds/removes than the threshold), let the user opt out — protects
        // against phantom-pending snapshots and similar accidents.
        if (_settings.ConfirmLargeUpdates && !ConfirmLargeChangeIfNeeded(snapshot))
            return;

        try
        {
            _liveSyncSuppressed = true;
            var result = FpbJsonToCaex.UpdateInPlace(_doc, snapshot, _ih);
            _pendingSnapshot = null;
            OnPendingStateChanged();
            foreach (var w in result.Warnings) PluginLog.Warn($"[{_ihLabel}] {w}");

            RunVdiValidationIfEnabled();
            _lastIhHash = ComputeIhHash(_ih);

            if (_settings.AutoSaveAfterUpdate && EditorSaver.TrySaveActiveDocument())
                SetStatus("Updated. Editor save command invoked (Ctrl+S still works manually).");
            else if (_settings.AutoSaveAfterUpdate)
                SetStatus("Updated. Auto-save unavailable — press Ctrl+S to persist.");
            else
                SetStatus("Updated. Press Ctrl+S to persist.");

            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[{_ihLabel}] Update failed", ex);
            MessageBox.Show($"Updating this InstanceHierarchy failed:\n\n{ex.Message}",
                "FPB.js Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _liveSyncSuppressed = false;
        }
    }

    /// <summary>
    /// Heuristic safety net: count elements / connections in the incoming snapshot
    /// and in the target IH; if the absolute difference for either count exceeds
    /// the threshold, show a confirmation dialog. Returns true when the caller may
    /// proceed (user confirmed OR diff was small enough OR check is disabled).
    /// </summary>
    private bool ConfirmLargeChangeIfNeeded(string snapshot)
    {
        if (_ih == null) return true;
        try
        {
            CountSnapshot(snapshot, out var snapElements, out var snapConnections);
            CountIh(_ih, out var ihElements, out var ihConnections);

            var elementDelta    = Math.Abs(snapElements    - ihElements);
            var connectionDelta = Math.Abs(snapConnections - ihConnections);
            var threshold       = Math.Max(1, _settings.UpdateSafetyThreshold);

            if (elementDelta <= threshold && connectionDelta <= threshold) return true;

            var msg = $"This update would significantly change InstanceHierarchy '{_ihLabel}':\n\n" +
                      $"  Elements:    AML has {ihElements}, snapshot has {snapElements} (Δ {elementDelta})\n" +
                      $"  Connections: AML has {ihConnections}, snapshot has {snapConnections} (Δ {connectionDelta})\n\n" +
                      "If you didn't intentionally add/remove this many items the snapshot may be stale " +
                      "(e.g. a phantom-pending state from a previous load).\n\n" +
                      "Apply this update anyway?";

            var result = MessageBox.Show(msg,
                "FPB.js — Confirm large InstanceHierarchy update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                PluginLog.Info($"[{_ihLabel}] Large-update confirmation declined: " +
                               $"snapshot={snapElements}/{snapConnections}, ih={ihElements}/{ihConnections}.");
                SetStatus("Update cancelled.");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // If the safety check itself throws, log and let the update proceed —
            // the user clicked Update intentionally and the diff might still apply.
            PluginLog.Error($"[{_ihLabel}] Safety-check failed; allowing update", ex);
            return true;
        }
    }

    private static void CountSnapshot(string json, out int elementCount, out int connectionCount)
    {
        elementCount = 0;
        connectionCount = 0;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("elementDataInformation", out var edi)) continue;
            foreach (var elem in edi.EnumerateArray())
            {
                var t = elem.TryGetProperty("$type", out var tp) ? tp.GetString() ?? "" : "";
                if (FpbMapper.Conversion.FpbMappings.ConnectionTypes.Contains(t))
                    connectionCount++;
                else
                    elementCount++;
            }
        }
    }

    private static void CountIh(InstanceHierarchyType ih, out int elementCount, out int connectionCount)
    {
        var elementCounter    = 0;
        var connectionCounter = 0;
        var fpdSucs = new HashSet<string>(FpbMapper.Conversion.FpbMappings.ElementToSuc.Values);

        void Walk(IEnumerable<InternalElementType> roots)
        {
            foreach (var ie in roots)
            {
                if (!string.IsNullOrEmpty(ie.RefBaseSystemUnitPath) && fpdSucs.Contains(ie.RefBaseSystemUnitPath))
                    elementCounter++;
                connectionCounter += ie.InternalLink.Count();
                Walk(ie.InternalElement);
            }
        }
        Walk(ih.InternalElement);
        elementCount    = elementCounter;
        connectionCount = connectionCounter;
    }

    // ── Refresh-Button: re-render this tab from the current AML state ─────
    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _ih == null) return;
        try
        {
            PushIhToWebView();
            _pendingSnapshot = null;
            OnPendingStateChanged();
            _lastIhHash = ComputeIhHash(_ih);
            SetStatus("Viewer re-rendered from current AML.");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[{_ihLabel}] Refresh failed", ex);
            SetStatus("Refresh failed: " + ex.Message);
        }
    }

    // ── Export-Button: write this IH's JSON to a file ─────────────────────
    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _ih == null) return;
        try
        {
            var result = CaexToFpbJson.Convert(_doc, _ih);
            var dialog = new SaveFileDialog
            {
                Filter = "FPB.js JSON (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                FileName = SanitiseFileName(_ihLabel) + ".json",
            };
            if (dialog.ShowDialog() != true) return;
            File.WriteAllText(dialog.FileName, result.Value, System.Text.Encoding.UTF8);
            PluginLog.Info($"[{_ihLabel}] Exported to {dialog.FileName}.");
            SetStatus($"Exported: {Path.GetFileName(dialog.FileName)}");
            foreach (var w in result.Warnings) PluginLog.Warn($"[{_ihLabel}] {w}");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[{_ihLabel}] Export failed", ex);
            MessageBox.Show($"Export failed:\n\n{ex.Message}",
                "FPB.js Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string SanitiseFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat(s.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(clean) ? "InstanceHierarchy" : clean;
    }

    // ── JS → host messages ────────────────────────────────────────────────
    private void OnDiagramChangedFromJs(JsonElement payload)
    {
        if (_disposed) return;
        if (payload.ValueKind == JsonValueKind.Undefined || payload.ValueKind == JsonValueKind.Null) return;
        if (DateTime.UtcNow - _lastImportFromHost < EchoSuppressionWindow) return;
        if (_doc == null) return;

        _pendingSnapshot = payload.GetRawText();
        OnPendingStateChanged();
        Dispatcher.BeginInvoke(() =>
            System.Windows.Input.CommandManager.InvalidateRequerySuggested());
    }

    private void OnPendingStateChanged()
    {
        try { PendingChanged?.Invoke(this, HasPendingSnapshot); }
        catch (Exception ex) { PluginLog.Error("PendingChanged handler threw", ex); }
    }

    // ── Live-sync polling: hash THIS IH only ──────────────────────────────
    private void StartLiveSync()
    {
        StopLiveSync();
        if (_doc == null || _ih == null) return;
        _lastIhHash = ComputeIhHash(_ih);
        _liveSyncTimer = new DispatcherTimer { Interval = LiveSyncPollInterval };
        _liveSyncTimer.Tick += LiveSyncTick;
        _liveSyncTimer.Start();
    }

    private void StopLiveSync()
    {
        if (_liveSyncTimer != null)
        {
            _liveSyncTimer.Stop();
            _liveSyncTimer.Tick -= LiveSyncTick;
            _liveSyncTimer = null;
        }
    }

    private void LiveSyncTick(object? sender, EventArgs e)
    {
        if (_disposed || _liveSyncSuppressed || _doc == null || _ih == null || _bridge == null) return;
        try
        {
            var hash = ComputeIhHash(_ih);
            if (hash == _lastIhHash) return;
            _lastIhHash = hash;

            // Conflict: user has unsynced FPB.js edits AND the AML tree changed.
            // Drop pending and warn so we don't later silently overwrite the tree.
            if (!string.IsNullOrEmpty(_pendingSnapshot))
            {
                PluginLog.Warn($"[{_ihLabel}] Live sync: AML changed while FPB.js had unsynced edits — pending viewer edits dropped.");
                SetStatus("Tree changed externally — pending viewer edits dropped.");
                _pendingSnapshot = null;
                OnPendingStateChanged();
            }

            PluginLog.Debug($"[{_ihLabel}] external change detected, refreshing viewer.");
            PushIhToWebView();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[{_ihLabel}] live-sync tick failed", ex);
        }
    }

    private static string ComputeIhHash(InstanceHierarchyType ih)
    {
        try
        {
            var xml = ih.Node?.ToString() ?? string.Empty;
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(xml)));
        }
        catch
        {
            return string.Empty;
        }
    }

    private void RunVdiValidationIfEnabled()
    {
        if (!_settings.RunVdiValidation || _doc == null) return;
        try
        {
            var w = Vdi3682Validator.Validate(_doc);
            if (w.Count == 0)
            {
                PluginLog.Debug($"[{_ihLabel}] VDI 3682 validation: clean.");
                return;
            }
            SetStatus($"VDI 3682: {w.Count} warning(s) — see verbose log.");
            foreach (var line in w) PluginLog.Warn($"VDI3682: {line}");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[{_ihLabel}] VDI validation threw", ex);
        }
    }

    private void SetStatus(string text)
    {
        if (StatusLabel == null) return;
        if (Dispatcher.CheckAccess()) StatusLabel.Text = text;
        else Dispatcher.BeginInvoke(new Action(() => StatusLabel.Text = text));
        PluginLog.Info($"[{_ihLabel}] {text}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopLiveSync();
        try { _bridge?.Dispose(); } catch { /* best effort */ }
        _bridge = null;
        _pendingSnapshot = null;
        _doc = null;
        _ih = null;
    }
}
