// One viewer-tab in the FPB.js plugin: renders a single InstanceHierarchy
// (one fpb:Project in CAEX terms), holds its own WebView2 + bridge + state.
//
// Lifecycle is owned by FpbPlugin which creates an IhView per FPD-bearing IH
// on DocumentLoaded and disposes them on DocumentUnLoaded.

using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private DateTime _pendingSnapshotTimestamp = DateTime.MinValue;
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

    /// <summary>
    /// Snapshot accessor for the host's per-IH pending-cache (used when the user
    /// switches between AML documents — the IhView is disposed but the unsaved
    /// snapshot should survive). Returns null when nothing is pending.
    /// </summary>
    public string? PeekPendingSnapshot() => _pendingSnapshot;

    /// <summary>
    /// Re-arm a previously-captured pending snapshot after BindAsync. Must be
    /// called only on a freshly bound view that doesn't already carry edits.
    /// </summary>
    public void RestorePendingSnapshot(string snapshot)
    {
        if (string.IsNullOrEmpty(snapshot)) return;
        _pendingSnapshot = snapshot;
        // The cache survived a doc switch but we have no original timestamp,
        // so treat the restore moment as the snapshot's age start. Worst case:
        // user gets the stale-snapshot warning slightly later than ideal.
        _pendingSnapshotTimestamp = DateTime.UtcNow;
        try { PendingChanged?.Invoke(this, HasPendingSnapshot); }
        catch (Exception ex) { PluginLog.Error("PendingChanged handler threw during restore", ex); }
        SetStatus("Restored pending edits from before the document switch — click Update InstanceHierarchy to apply, Refresh from AML to discard.");
    }

    /// <summary>VDI 3682 findings shown in the bottom DataGrid. Refreshed at every Update / Refresh cycle.</summary>
    private readonly ObservableCollection<FindingRow> _findings = new();

    public IhView()
    {
        InitializeComponent();
        FindingsGrid.ItemsSource = _findings;
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
        _bridge.OnError += msg =>
        {
            PluginLog.Error($"[{_ihLabel}] viewer: {msg}");
            // Surface JS-side render errors (e.g. "Cannot read properties of
            // undefined (reading 'type')" in buildSystemLimit) directly to the
            // user so they don't end up staring at an empty canvas wondering
            // what happened.
            ShowJsErrorBanner("The viewer hit a rendering error. " +
                              "The diagram may be incomplete. Try Refresh from AML, or check the log file for details.");
        };
        _bridge.OnDiagramChanged += OnDiagramChangedFromJs;
        _bridge.OnImported += () =>
        {
            // Do NOT re-stamp _lastImportFromHost here. The pre-stamp in
            // PushIhToWebView (line ~170) defines the start of the echo-
            // suppression window. Re-stamping on a delayed ack (slow
            // machines / WebView2 queue) would EXTEND that window past its
            // intended 3-second duration and silently swallow a genuine user
            // edit made just after the import landed.
            PluginLog.Debug($"[{_ihLabel}] import acknowledged by JS (pre-stamp at PushIhToWebView remains the echo-window anchor).");
            HideJsErrorBanner();
        };
        _bridge.OnJsLog += (lvl, msg) => PluginLog.FromJs(lvl, $"[{_ihLabel}] {msg}");

        try { await _bridge.InitAsync(); }
        catch (Exception ex) { PluginLog.Error($"[{_ihLabel}] WebView2 init failed", ex); }

        StartLiveSync();
    }

    public void SendTheme(string theme) => _bridge?.SendTheme(theme);

    private void ShowJsErrorBanner(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowJsErrorBanner(message));
            return;
        }
        if (JsErrorBanner == null || JsErrorMessage == null) return;
        JsErrorMessage.Text = message;
        JsErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideJsErrorBanner()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(HideJsErrorBanner);
            return;
        }
        if (JsErrorBanner == null) return;
        JsErrorBanner.Visibility = Visibility.Collapsed;
    }

    private void JsErrorRetryButton_Click(object sender, RoutedEventArgs e)
    {
        HideJsErrorBanner();
        if (_doc != null && _ih != null) PushIhToWebView();
    }

    /// <summary>Push this IH's content into the viewer (used after AML-side edits).</summary>
    public void PushIhToWebView()
    {
        if (_disposed || _bridge == null || _doc == null || _ih == null) return;
        try
        {
            var result = CaexToFpbJson.Convert(_doc, _ih);
            // Stamp AFTER ImportJson returns successfully, not before. If
            // ImportJson silently short-circuits (bridge not ready,
            // CoreWebView2 null) or PostWebMessageAsJson throws, a pre-stamp
            // would open a fake 3-second echo-suppression window during
            // which a genuine user edit gets silently dropped. With the
            // stamp after, a failed push leaves the window closed; legit
            // edits land.
            _bridge.ImportJson(result.Value);
            _lastImportFromHost = DateTime.UtcNow;
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
        // Every other entry point in this class checks _disposed first.
        if (_disposed || _doc == null || _ih == null) return;
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

        // Stale-snapshot guard: if the pending edit lingered across a long
        // modelling break (e.g. left the editor open over lunch), warn the
        // user before applying it. Same rationale as ConfirmLargeUpdates —
        // narrow the window where forgotten/stale state silently overwrites
        // edits made elsewhere.
        if (!ConfirmPendingAgeIfNeeded()) return;

        // Disable the button + set a busy status so the user gets immediate
        // feedback while UpdateInPlace runs (still synchronous on the UI thread
        // because Aml.Engine is not thread-safe). Each phase is stopwatched and
        // surfaced in the log so spikes become diagnosable.
        if (UpdateButton != null) UpdateButton.IsEnabled = false;
        SetStatus($"Updating '{_ihLabel}' …");
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _liveSyncSuppressed = true;

            // Snapshot meta-info so we can correlate post-update behaviour with
            // what the viewer actually emitted. The hash is just a cheap finger-
            // print, not a full content dump.
            var snapshotHash = snapshot.GetHashCode().ToString("X8");
            PluginLog.Debug($"[{_ihLabel}] Update: snapshot bytes={snapshot.Length} hash={snapshotHash}");

            // Trace callback so every mapper operation (ResolveProcess /
            // UpdateElement / AddConnection / RemoveOrphans / SetCharacteristics
            // / …) lands in the log as attempt + result lines. Lets us
            // reconstruct exactly what the mapper did during one update.
            var mapperOptions = new MapperOptions
            {
                Trace = line => PluginLog.Debug($"[{_ihLabel}] mapper: {line}"),
            };

            var swMapper = System.Diagnostics.Stopwatch.StartNew();
            var result = FpbJsonToCaex.UpdateInPlace(_doc, snapshot, _ih, mapperOptions);
            swMapper.Stop();
            // A re-entrant ChangeSelectedObject during the mapper call can
            // have disposed this view. Bail out before touching now-null
            // _doc/_ih any further.
            if (_disposed) { PluginLog.Debug($"[{_ihLabel}] Update aborted post-mapper — view disposed during operation."); return; }

            _pendingSnapshot = null;
        _pendingSnapshotTimestamp = DateTime.MinValue;
            OnPendingStateChanged();
            foreach (var w in result.Warnings) PluginLog.Warn($"[{_ihLabel}] {w}");

            var swValidator = System.Diagnostics.Stopwatch.StartNew();
            RunVdiValidationIfEnabled();
            swValidator.Stop();
            if (_disposed) { PluginLog.Debug($"[{_ihLabel}] Update aborted post-validator — view disposed."); return; }

            var swHash = System.Diagnostics.Stopwatch.StartNew();
            _lastIhHash = ComputeIhHash(_ih);
            swHash.Stop();

            var swSave = System.Diagnostics.Stopwatch.StartNew();
            var saveAttempted = _settings.AutoSaveAfterUpdate;
            // EditorSaver may trigger ChangeSelectedObject as a side-effect,
            // which can dispose this view mid-call. Check after.
            var saveOk        = saveAttempted && EditorSaver.TrySaveActiveDocument();
            swSave.Stop();
            if (_disposed) { PluginLog.Debug($"[{_ihLabel}] Update aborted post-save — view disposed (likely by editor's save-side-effect)."); return; }

            if (saveOk)
                SetStatus("Updated. Editor save command invoked (Ctrl+S still works manually).");
            else if (saveAttempted)
                SetStatus("Updated. Auto-save unavailable — press Ctrl+S to persist.");
            else
                SetStatus("Updated. Press Ctrl+S to persist.");

            swTotal.Stop();
            PluginLog.Info($"[{_ihLabel}] Update timings — " +
                $"mapper:{swMapper.ElapsedMilliseconds}ms " +
                $"validator:{swValidator.ElapsedMilliseconds}ms " +
                $"hash:{swHash.ElapsedMilliseconds}ms " +
                $"save:{swSave.ElapsedMilliseconds}ms " +
                $"total:{swTotal.ElapsedMilliseconds}ms");

            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            // Mirror the in-try _disposed guards. If the view was disposed
            // via a re-entrant ChangeSelectedObject during the update,
            // showing a dialog for a now-orphaned operation just confuses
            // the user.
            if (_disposed)
            {
                PluginLog.Debug($"[{_ihLabel}] Update threw after view disposal — suppressing UI dialog. Inner: {ex.Message}");
                return;
            }
            PluginLog.Error($"[{_ihLabel}] Update failed", ex);
            MessageBox.Show($"Updating this InstanceHierarchy failed:\n\n{ex.Message}",
                "FPB.js Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Even after Dispose, leaving _liveSyncSuppressed=true would
            // affect nothing on this object (the timer is gone), but resetting
            // is harmless. The UpdateButton may have been GC'd along with the
            // visual tree; the null-check handles that.
            _liveSyncSuppressed = false;
            if (!_disposed && UpdateButton != null) UpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Returns true when the pending snapshot is fresh enough OR the user
    /// confirmed applying a stale one OR the check is disabled in settings.
    /// </summary>
    private bool ConfirmPendingAgeIfNeeded()
    {
        var thresholdMinutes = _settings.PendingAgeWarningMinutes;
        if (thresholdMinutes <= 0) return true;
        if (_pendingSnapshotTimestamp == DateTime.MinValue) return true;

        var age = DateTime.UtcNow - _pendingSnapshotTimestamp;
        if (age.TotalMinutes < thresholdMinutes) return true;

        var ageDesc = age.TotalHours >= 1
            ? $"{age.TotalHours:F1} hour(s)"
            : $"{age.TotalMinutes:F0} minute(s)";
        var msg = $"The pending viewer edit for '{_ihLabel}' is {ageDesc} old.\n\n" +
                  "Apply it now? If you don't remember making it, choose No and " +
                  "use Refresh from AML to discard the snapshot.";
        var result = MessageBox.Show(msg,
            "FPB.js — Stale pending edit",
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
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
            // Audit-fix #9: log before/after so a user reporting "Refresh did
            // nothing" can verify from the log whether the conversion actually
            // produced the same content or got dropped on the WebView2 floor.
            var hashBefore = _lastIhHash;
            var hadPending = _pendingSnapshot != null;
            PluginLog.Debug($"[{_ihLabel}] Refresh: pending={(hadPending ? "discarded" : "none")} hash-before={hashBefore:X8}");

            PushIhToWebView();
            _pendingSnapshot = null;
        _pendingSnapshotTimestamp = DateTime.MinValue;
            OnPendingStateChanged();
            _lastIhHash = ComputeIhHash(_ih);

            PluginLog.Debug($"[{_ihLabel}] Refresh: hash-after={_lastIhHash:X8} changed={(hashBefore != _lastIhHash)}");
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
        _pendingSnapshotTimestamp = DateTime.UtcNow;
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
            var hashBefore = _lastIhHash;
            _lastIhHash = hash;

            // Conflict: user has unsynced FPB.js edits AND the AML tree changed.
            // Drop pending and warn so we don't later silently overwrite the tree.
            // Before dropping, dump the snapshot to a TEMP backup so the user has
            // a recovery path if the discarded edits were valuable.
            if (!string.IsNullOrEmpty(_pendingSnapshot))
            {
                var backupPath = TryWritePendingBackup(_pendingSnapshot);
                var locationHint = backupPath is null
                    ? string.Empty
                    : $" Backup written to {backupPath}.";

                PluginLog.Warn($"[{_ihLabel}] Live sync: AML changed while FPB.js had unsynced edits — pending viewer edits dropped." +
                               locationHint +
                               $" hash-before={hashBefore:X8} hash-now={hash:X8} pending-bytes={_pendingSnapshot.Length}");
                SetStatus("Tree changed externally — pending viewer edits dropped." + locationHint);
                _pendingSnapshot = null;
                _pendingSnapshotTimestamp = DateTime.MinValue;
                OnPendingStateChanged();
            }
            else
            {
                PluginLog.Debug($"[{_ihLabel}] Live sync: AML hash changed {hashBefore:X8} -> {hash:X8} (no pending edits to drop)");
            }

            PluginLog.Debug($"[{_ihLabel}] external change detected, refreshing viewer.");
            PushIhToWebView();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[{_ihLabel}] live-sync tick failed", ex);
        }
    }

    /// <summary>
    /// Persist a pending FPB.js snapshot to <c>%TEMP%\fpb-plugin\pending-backup\</c>
    /// when live-sync is about to discard it because the AML tree changed
    /// externally. Returns the backup file path on success, null on failure
    /// (the calling path stays running either way — recovery is best-effort).
    /// </summary>
    private string? TryWritePendingBackup(string snapshotJson)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "fpb-plugin", "pending-backup");
            Directory.CreateDirectory(dir);
            var fileName = $"pending-{SanitiseFileName(_ihLabel)}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, snapshotJson, System.Text.Encoding.UTF8);
            return path;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[{_ihLabel}] Pending-snapshot backup failed: {ex.Message}");
            return null;
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
        catch (Exception ex)
        {
            // Silently returning "" used to mask repeated
            // SHA256 failures — LiveSyncTick's `hash == _lastIhHash` check
            // would then see ""=="" and skip refresh forever. Log the failure
            // so it's at least diagnosable.
            PluginLog.Warn($"ComputeIhHash failed (next LiveSyncTick will treat tree as unchanged): {ex.Message}");
            return string.Empty;
        }
    }

    private void RunVdiValidationIfEnabled()
    {
        if (!_settings.RunVdiValidation || _doc == null)
        {
            UpdateFindingsUi(Array.Empty<ValidationFinding>());
            return;
        }
        try
        {
            var options = BuildValidationOptions(_settings);
            var findings = Vdi3682Validator.ValidateStructured(_doc, options);
            UpdateFindingsUi(findings);

            if (findings.Count == 0)
            {
                PluginLog.Debug($"[{_ihLabel}] VDI 3682 validation: clean.");
                return;
            }
            var errors   = findings.Count(f => f.Severity == ValidationSeverity.Error);
            var warnings = findings.Count(f => f.Severity == ValidationSeverity.Warning);
            var infos    = findings.Count(f => f.Severity == ValidationSeverity.Info);
            SetStatus($"VDI 3682: {errors} error(s), {warnings} warning(s), {infos} info — see Findings panel.");
            foreach (var f in findings)
            {
                var prefix = $"VDI3682 [{f.Severity}] {f.RuleId}";
                if (f.Severity == ValidationSeverity.Error)   PluginLog.Error($"{prefix}: {f.Message}");
                else if (f.Severity == ValidationSeverity.Warning) PluginLog.Warn($"{prefix}: {f.Message}");
                else PluginLog.Info($"{prefix}: {f.Message}");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[{_ihLabel}] VDI validation threw", ex);
        }
    }

    /// <summary>
    /// Mirror the latest findings into the bottom DataGrid + the summary label.
    /// Marshalled to the UI thread because validation may run off-thread later.
    /// </summary>
    private void UpdateFindingsUi(IReadOnlyList<ValidationFinding> findings)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateFindingsUi(findings));
            return;
        }

        _findings.Clear();
        foreach (var f in findings) _findings.Add(FindingRow.From(f, _doc));

        if (findings.Count == 0)
        {
            FindingsSummary.Text = " — no findings";
        }
        else
        {
            var e = findings.Count(f => f.Severity == ValidationSeverity.Error);
            var w = findings.Count(f => f.Severity == ValidationSeverity.Warning);
            var i = findings.Count(f => f.Severity == ValidationSeverity.Info);
            FindingsSummary.Text = $" — {e} error · {w} warning · {i} info";
        }
    }

    /// <summary>
    /// Double-click on a finding asks the viewer to focus the corresponding
    /// element. Bridge.SelectElement no-ops gracefully when the id is unknown
    /// to the JS side (e.g. a project-level finding without an element id).
    /// </summary>
    private void FindingsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_disposed || _bridge == null) return;
        if (FindingsGrid.SelectedItem is not FindingRow row) return;
        if (string.IsNullOrEmpty(row.ElementId)) return;
        _bridge.SelectElement(row.ElementId);
    }

    private static ValidationOptions BuildValidationOptions(PluginSettings settings)
    {
        var minSeverity = Enum.TryParse<ValidationSeverity>(settings.ValidationMinSeverity, ignoreCase: true, out var ms)
            ? ms : ValidationSeverity.Info;
        return new ValidationOptions
        {
            DisabledRuleIds = new HashSet<string>(settings.DisabledValidationRuleIds, StringComparer.OrdinalIgnoreCase),
            MinimumSeverity = minSeverity,
        };
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
        _pendingSnapshotTimestamp = DateTime.MinValue;
        _doc = null;
        _ih = null;
    }
}
