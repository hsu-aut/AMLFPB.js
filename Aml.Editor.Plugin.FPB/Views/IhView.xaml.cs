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

    /// <summary>
    /// The viewer's own export of the model we last imported into it (shipped
    /// with the JS 'imported' ack). 'changed' payloads equal to this are import
    /// fallout; different payloads are genuine user edits. Null between a push
    /// and its ack — the timed window covers that gap.
    /// </summary>
    private string? _echoBaseline;
    private bool _liveSyncSuppressed;
    private bool _disposed;
    // why the last ApplyPending failed, for the status of Ctrl+S
    private string? _lastApplyError;

    // the two checks of the findings list (V25)
    private IReadOnlyList<ValidationFinding> _pluginFindings = Array.Empty<ValidationFinding>();
    private IReadOnlyList<PageFinding> _pageFindings = Array.Empty<PageFinding>();
    private HashSet<string> _pageRules = new(StringComparer.Ordinal);

    // 016f E3, V28: what the tab does when the InstanceHierarchy changes in the editor
    private readonly SyncState _sync = new();

    /// <summary>The bar of a conflict stands (V28).</summary>
    private bool InConflict => _sync.Phase == SyncPhase.Conflict;

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

    /// <summary>
    /// Raised when an element is selected in the diagram that has an object in
    /// the InstanceHierarchy (016f E3, V26); the plugin selects it in the AML tree.
    /// </summary>
    public event Action<IhView, CAEXObject>? ElementSelected;

    // which object is which element on which layer (V26), rebuilt with every
    // load and every apply
    private FpdElementIndex _index = FpdElementIndex.Empty;

    /// <summary>Raised when the user switches View only in this tab (the host applies it to all tabs and remembers it).</summary>
    public event Action<IhView, bool>? ViewOnlyChanged;

    // set while the host applies the state, so the toggle does not report it back
    private bool _applyingViewOnly;

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
        _sync.DiagramChanged(equalsBaseline: false);
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
        // A file dropped on the diagram opens nothing (V14). Set here, not in
        // XAML: in the editor the XAML loader took the property from another
        // plugin's copy of the WebView2 library and threw, so no tab was built.
        WebView.AllowExternalDrop = false;
    }

    /// <summary>Whether a file dropped on the diagram reaches the page (V14: no).</summary>
    public bool AcceptsDroppedFiles => WebView.AllowExternalDrop;

    /// <summary>
    /// Hands the view the plugin's document-level commands for its Process menu.
    /// The plugin owns them, since they add an InstanceHierarchy rather than act
    /// on this one; running the same command objects keeps enabling and disabling
    /// in step with the editor toolbar.
    /// </summary>
    public void UseDocumentCommands(System.Windows.Input.ICommand newProcess, System.Windows.Input.ICommand import)
    {
        NewProcessItem.Command = newProcess;
        ImportItem.Command = import;
    }

    /// <summary>Opens the Process menu under its button, the way a drop-down button does.</summary>
    private void ProcessMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ProcessMenu.PlacementTarget = ProcessMenuButton;
        ProcessMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ProcessMenu.IsOpen = true;
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
        _bridge.OnSaveRequested += OnSaveFromPage;
        _bridge.OnSelected += OnSelectedInDiagram;
        _bridge.OnFindings += OnFindingsFromPage;
        // the markers of FPB.js go with "Run VDI validation" (V25)
        _bridge.SetValidationMarkers(settings.RunVdiValidation);
        _bridge.OnImported += baseline =>
        {
            // Content-based echo detection (P0-8): the ack carries the viewer's
            // own export of the just-imported model. Any later 'changed' payload
            // equal to this baseline is import fallout; anything else is a REAL
            // user edit — no timed window that could swallow genuine edits.
            _echoBaseline = baseline;
            PluginLog.Debug($"[{_ihLabel}] import acknowledged by JS " +
                            (baseline != null ? "(echo baseline captured)." : "(no baseline payload — timed fallback stays active)."));
            HideJsErrorBanner();
        };
        _bridge.OnJsLog += (lvl, msg) => PluginLog.FromJs(lvl, $"[{_ihLabel}] {msg}");
        ApplyViewOnly(settings.ViewOnly);
        StartLiveSync();

        // Not awaited: WebView2 starts only once its control has a window, that
        // is once this tab is shown. A tab not selected, or the plugin's pane
        // behind another one when the document opened, has none; awaiting here
        // held up the later tabs and the selection of the first, and the plugin
        // stayed empty (016f E3, run in the real editor). Until the page is
        // ready, the bridge buffers what is sent.
        _ = InitBridgeAsync(settings.DebugLogging);
        await Task.CompletedTask;
    }

    private async Task InitBridgeAsync(bool debugTools)
    {
        // DevTools and the browser's context menu only for "Verbose debug logging" (V16)
        try { if (_bridge != null) await _bridge.InitAsync(debugTools); }
        catch (Exception ex) { PluginLog.Error($"[{_ihLabel}] WebView2 init failed", ex); }
    }

    public void SendTheme(string theme) => _bridge?.SendTheme(theme);

    /// <summary>Show the state on the toggle and hand it to the viewer, without raising <see cref="ViewOnlyChanged"/>.</summary>
    public void ApplyViewOnly(bool viewOnly)
    {
        _applyingViewOnly = true;
        try
        {
            if (ViewOnlyToggle != null) ViewOnlyToggle.IsChecked = viewOnly;
        }
        finally
        {
            _applyingViewOnly = false;
        }
        _bridge?.SetReadOnly(viewOnly);
    }

    private void ViewOnlyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingViewOnly) return;
        ViewOnlyChanged?.Invoke(this, ViewOnlyToggle.IsChecked == true);
    }

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
            _index = FpdElementIndex.Build(_ih);
            // Stamp AFTER ImportJson returns successfully, not before. If
            // ImportJson silently short-circuits (bridge not ready,
            // CoreWebView2 null) or PostWebMessageAsJson throws, a pre-stamp
            // would open a fake 3-second echo-suppression window during
            // which a genuine user edit gets silently dropped. With the
            // stamp after, a failed push leaves the window closed; legit
            // edits land.
            // Only arm the echo-suppression window when the payload actually
            // reached the live page. If ImportJson merely buffered it (bridge
            // not ready yet), stamping here would open a fake window that
            // swallows a genuine user edit made before the buffered push lands.
            var posted = _bridge.ImportJson(result.Value);
            if (posted)
            {
                _lastImportFromHost = DateTime.UtcNow;
                // Invalidate the previous baseline until the new import is
                // acknowledged — the timed window bridges that short gap.
                _echoBaseline = null;
            }
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

            // Validate on every push (initial load, Refresh, live-sync), not
            // only after Update — otherwise the findings panel sits empty until
            // the user first writes something back.
            RunVdiValidationIfEnabled();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[{_ihLabel}] conversion failed", ex);
            SetStatus("Conversion failed: " + ex.Message);
        }
    }

    // ── Update-Button: push pending FPB.js state into the IH ──────────────
    // The buttons act a moment later, at Background priority: a field of the
    // panel writes its text when it loses the focus to the button, and that
    // change should arrive before the snapshot is read (final review of 016f E3).
    private void UpdateButton_Click(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(UpdateNow), DispatcherPriority.Background);

    private void UpdateNow()
    {
        // Every other entry point in this class checks _disposed first.
        if (_disposed || _doc == null || _ih == null) return;
        if (string.IsNullOrEmpty(_pendingSnapshot))
        {
            SetStatus("No pending viewer changes to apply.");
            return;
        }
        if (ApplyPending() != ApplyResult.Applied || _disposed) return;

        var swSave = System.Diagnostics.Stopwatch.StartNew();
        var saveAttempted = _settings.AutoSaveAfterUpdate;
        // EditorSaver may trigger ChangeSelectedObject as a side-effect,
        // which can dispose this view mid-call. Check after.
        var saveOk = saveAttempted && EditorSaver.TrySaveActiveDocument();
        swSave.Stop();
        if (_disposed) { PluginLog.Debug($"[{_ihLabel}] Update aborted post-save — view disposed (likely by editor's save-side-effect)."); return; }
        if (saveAttempted) PluginLog.Info($"[{_ihLabel}] Update save: {swSave.ElapsedMilliseconds}ms");

        if (saveOk)
            SetStatus("Updated. Editor save command invoked (Ctrl+S still works manually).");
        else if (saveAttempted)
            SetStatus("Updated. Auto-save unavailable — press Ctrl+S to persist.");
        else
            SetStatus("Updated. Press Ctrl+S to persist.");
    }

    /// <summary>
    /// Ctrl+S in the diagram (016f E3, V22): the page sends the model of that
    /// moment; it is taken like a change, applied the way "Update
    /// InstanceHierarchy" applies, and the AML file is saved through the editor.
    /// </summary>
    private void OnSaveFromPage(JsonElement data)
    {
        if (_disposed || _doc == null || _ih == null) return;
        OnDiagramChangedFromJs(data);
        _lastApplyError = null;
        var outcome = SaveFlow.Run(HasPendingSnapshot, InConflict, ApplyPending, EditorSaver.TrySaveActiveDocument);
        if (_disposed) return;
        SetStatus(SaveFlow.StatusText(outcome, _lastApplyError));
    }

    // ── Selection between the AML tree and the diagram (V26) ──────────────

    /// <summary>
    /// Show in the diagram what was selected in the AML tree: the nearest object
    /// upwards that the diagram has (an interface or attribute selects its
    /// element). Returns the id of that object, or null when the diagram has none.
    /// </summary>
    public string? SelectFromEditor(CAEXBasicObject selected)
    {
        if (_disposed || _bridge == null) return null;
        for (var o = selected; o != null; o = o.CAEXParent as CAEXBasicObject)
        {
            if (o is InstanceHierarchyType) return null;
            if (o is CAEXObject withId && _index.TryGetFpb(withId.ID, out var fpbId, out var processId))
            {
                _bridge.Select(fpbId, processId);
                return withId.ID;
            }
        }
        return null;
    }

    private void OnSelectedInDiagram(string? id, string? processId)
    {
        if (_disposed || _ih == null || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(processId)) return;
        var objectId = _index.IeIdFor(id, processId);
        // drawn after the last apply: no object in the tree yet
        if (objectId == null) return;
        var found = FindObject(objectId);
        if (found == null) return;
        try { ElementSelected?.Invoke(this, found); }
        catch (Exception ex) { PluginLog.Error("ElementSelected handler threw", ex); }
    }

    /// <summary>The InternalElement or InternalLink of the InstanceHierarchy with this id (without braces).</summary>
    private CAEXObject? FindObject(string bareId)
    {
        if (_ih == null) return null;
        static bool Same(string? id, string bare) => string.Equals((id ?? "").Trim('{', '}'), bare, StringComparison.OrdinalIgnoreCase);
        foreach (var process in _ih.InternalElement)
        {
            if (Same(process.ID, bareId)) return process;
            foreach (var ie in process.InternalElement)
                if (Same(ie.ID, bareId)) return ie;
            foreach (var link in process.InternalLink)
                if (Same(link.ID, bareId)) return link;
        }
        return null;
    }

    /// <summary>Give the keyboard back to the diagram (the editor focuses the plugin after a selection).</summary>
    public void FocusDiagram()
    {
        if (!_disposed) WebView?.Focus();
    }

    /// <summary>A text for the status line of this tab from outside (the plugin after a save of the editor).</summary>
    public void ShowStatus(string text) => SetStatus(text);

    /// <summary>
    /// Write the pending snapshot of the viewer into the InstanceHierarchy, with
    /// the questions of "Update InstanceHierarchy" (large change, old snapshot).
    /// Saving the file is up to the caller. Nothing pending counts as applied.
    /// </summary>
    public ApplyResult ApplyPending()
    {
        if (_disposed || _doc == null || _ih == null) return ApplyResult.Failed;
        var snapshot = _pendingSnapshot;
        if (string.IsNullOrEmpty(snapshot)) return ApplyResult.Applied;

        // Suppress live-sync for the ENTIRE operation, confirm dialogs included.
        // MessageBox.Show pumps the dispatcher, so without this the poll timer
        // could fire while a dialog is open, see an external tree change, and
        // drop _pendingSnapshot — after which we'd apply our local `snapshot`
        // copy and silently clobber that external edit. The finally block always
        // clears the flag, including on the dialog-declined early returns.
        _liveSyncSuppressed = true;
        System.Diagnostics.Stopwatch? swTotal = null;
        try
        {
            // Safety check before mutating the document. If the diff looks suspicious
            // (more adds/removes than the threshold), let the user opt out — protects
            // against phantom-pending snapshots and similar accidents.
            if (_settings.ConfirmLargeUpdates && !ConfirmLargeChangeIfNeeded(snapshot))
                return ApplyResult.Cancelled;

            // Stale-snapshot guard: if the pending edit lingered across a long
            // modelling break (e.g. left the editor open over lunch), warn the
            // user before applying it. Same rationale as ConfirmLargeUpdates —
            // narrow the window where forgotten/stale state silently overwrites
            // edits made elsewhere.
            if (!ConfirmPendingAgeIfNeeded()) return ApplyResult.Cancelled;

            // Disable the button + set a busy status so the user gets immediate
            // feedback while UpdateInPlace runs (still synchronous on the UI thread
            // because Aml.Engine is not thread-safe). Each phase is stopwatched and
            // surfaced in the log so spikes become diagnosable.
            if (UpdateButton != null) UpdateButton.IsEnabled = false;
            SetStatus($"Updating '{_ihLabel}' …");
            swTotal = System.Diagnostics.Stopwatch.StartNew();

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
            if (_disposed) { PluginLog.Debug($"[{_ihLabel}] Update aborted post-mapper — view disposed during operation."); return ApplyResult.Failed; }

            // a newer state that arrived during the questions stays pending
            _pendingSnapshot = SyncState.PendingAfterApply(snapshot, _pendingSnapshot);
            if (_pendingSnapshot == null) _pendingSnapshotTimestamp = DateTime.MinValue;
            OnPendingStateChanged();
            _sync.Applied();
            if (_pendingSnapshot != null) _sync.DiagramChanged(equalsBaseline: false);
            UpdateConflictBar();
            _index = FpdElementIndex.Build(_ih);
            // Re-anchor the echo baseline on the state we just wrote into the
            // document. Viewer == document == baseline now; a later undo back
            // to the PRE-update state differs from this baseline and correctly
            // becomes pending again. Leaving the old baseline in place would
            // classify exactly that undo as import fallout — silent revert loss.
            _echoBaseline = snapshot;
            foreach (var w in result.Warnings) PluginLog.Warn($"[{_ihLabel}] {w}");

            var swValidator = System.Diagnostics.Stopwatch.StartNew();
            RunVdiValidationIfEnabled();
            swValidator.Stop();
            if (_disposed) { PluginLog.Debug($"[{_ihLabel}] Update aborted post-validator — view disposed."); return ApplyResult.Failed; }

            var swHash = System.Diagnostics.Stopwatch.StartNew();
            _lastIhHash = ComputeIhHash(_ih);
            swHash.Stop();

            swTotal.Stop();
            PluginLog.Info($"[{_ihLabel}] Update timings — " +
                $"mapper:{swMapper.ElapsedMilliseconds}ms " +
                $"validator:{swValidator.ElapsedMilliseconds}ms " +
                $"hash:{swHash.ElapsedMilliseconds}ms " +
                $"total:{swTotal.ElapsedMilliseconds}ms");

            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            return ApplyResult.Applied;
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
                return ApplyResult.Failed;
            }
            _lastApplyError = ex.Message;
            PluginLog.Error($"[{_ihLabel}] Update failed", ex);
            MessageBox.Show($"Updating this InstanceHierarchy failed:\n\n{ex.Message}",
                "FPB.js Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return ApplyResult.Failed;
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
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => ReloadFromAml();

    // ── The bar of V28 ────────────────────────────────────────────────────
    private void ApplyDiagramButton_Click(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(ApplyDiagramNow), DispatcherPriority.Background);

    private void ApplyDiagramNow()
    {
        if (_disposed || _doc == null || _ih == null) return;
        // the diagram went back to what was loaded: nothing of it to keep
        if (!HasPendingSnapshot)
        {
            ReloadFromAml();
            return;
        }
        if (ApplyPending() == ApplyResult.Applied && !_disposed)
            SetStatus("Applied the diagram to the InstanceHierarchy. Press Ctrl+S to save the file.");
    }

    private void ReloadFromAmlButton_Click(object sender, RoutedEventArgs e) => ReloadFromAml();

    private void UpdateConflictBar()
    {
        if (ConflictBar != null) ConflictBar.Visibility = _sync.BarVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Load the diagram from the AML again ("Refresh from AML", "Reload from
    /// AML"). Changes of the diagram that are not applied are lost; they are
    /// kept as a backup in %TEMP% first.
    /// </summary>
    private void ReloadFromAml()
    {
        if (_doc == null || _ih == null) return;
        try
        {
            // Audit-fix #9: log before/after so a user reporting "Refresh did
            // nothing" can verify from the log whether the conversion actually
            // produced the same content or got dropped on the WebView2 floor.
            var hashBefore = _lastIhHash;
            var pending = _pendingSnapshot;
            PluginLog.Debug($"[{_ihLabel}] Refresh: pending={(pending != null ? "discarded" : "none")} hash-before={hashBefore:X8}");
            var backupPath = pending != null ? TryWritePendingBackup(pending) : null;

            PushIhToWebView();
            _pendingSnapshot = null;
            _pendingSnapshotTimestamp = DateTime.MinValue;
            OnPendingStateChanged();
            _sync.Reloaded();
            UpdateConflictBar();
            _lastIhHash = ComputeIhHash(_ih);

            PluginLog.Debug($"[{_ihLabel}] Refresh: hash-after={_lastIhHash:X8} changed={(hashBefore != _lastIhHash)}");
            SetStatus("Viewer re-rendered from current AML." + (backupPath != null ? $" Backup of the dropped diagram changes: {backupPath}" : ""));
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

    // ── Conformance-Button: emit a machine-readable VDI 3682 report ───────
    //
    // The report runs against the *full* CAEX document (not just this IH) so
    // CI uploads cover everything an AML file declares. The button itself
    // lives on this tab because that's where the user is when they look at
    // findings.
    private void ConformanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        try
        {
            var options = BuildValidationOptions(_settings);
            var report = ConformanceReportBuilder.Build(_doc, DateTimeOffset.Now, options);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dialog = new SaveFileDialog
            {
                Filter = "Conformance Report (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                FileName = $"{SanitiseFileName(_ihLabel)}-conformance-{stamp}.json",
            };
            if (dialog.ShowDialog() != true) return;

            File.WriteAllText(dialog.FileName, report.ToJson(), System.Text.Encoding.UTF8);
            PluginLog.Info($"[{_ihLabel}] Conformance report written to {dialog.FileName} " +
                           $"(errors={report.Totals.Errors}, warnings={report.Totals.Warnings}, infos={report.Totals.Infos}, clean={report.Totals.Clean}).");
            SetStatus($"Conformance report: {Path.GetFileName(dialog.FileName)} " +
                      $"(E:{report.Totals.Errors} W:{report.Totals.Warnings} I:{report.Totals.Infos}).");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[{_ihLabel}] Conformance export failed", ex);
            MessageBox.Show($"Writing the conformance report failed:\n\n{ex.Message}",
                "FPB.js Conformance Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── JS → host messages ────────────────────────────────────────────────
    private void OnDiagramChangedFromJs(JsonElement payload)
    {
        if (_disposed) return;
        if (payload.ValueKind == JsonValueKind.Undefined || payload.ValueKind == JsonValueKind.Null) return;
        if (_doc == null) return;

        var raw = payload.GetRawText();
        if (_echoBaseline != null)
        {
            // Content-based: identical to what the viewer exported right after
            // OUR import ⇒ fallout, not a user edit. Different content is a
            // real edit and is kept even seconds after an import.
            if (string.Equals(raw, _echoBaseline, StringComparison.Ordinal))
            {
                // Viewer is back IN SYNC with the document (e.g. the user
                // undid their edit). A stale pending snapshot from before the
                // undo must not survive — Update would write the already-
                // reverted edit into the AML.
                _sync.DiagramChanged(equalsBaseline: true);
                if (_pendingSnapshot != null)
                {
                    PluginLog.Debug($"[{_ihLabel}] viewer returned to baseline — clearing stale pending snapshot.");
                    _pendingSnapshot = null;
                    _pendingSnapshotTimestamp = DateTime.MinValue;
                    OnPendingStateChanged();
                    Dispatcher.BeginInvoke(() =>
                        System.Windows.Input.CommandManager.InvalidateRequerySuggested());
                }
                return;
            }
        }
        else if (DateTime.UtcNow - _lastImportFromHost < EchoSuppressionWindow)
        {
            // Fallback for the gap before the ack lands (or stale JS assets
            // without the baseline payload).
            return;
        }

        _pendingSnapshot = raw;
        _pendingSnapshotTimestamp = DateTime.UtcNow;
        _sync.DiagramChanged(equalsBaseline: false);
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

            // 016f E3, V28: a change in the editor never drops edits of the
            // diagram. Without pending edits the diagram reloads quietly; with
            // them the bar asks, and nothing reloads until the user chooses.
            switch (_sync.TreeChanged())
            {
                case TreeChangeAction.Reload:
                    PluginLog.Debug($"[{_ihLabel}] Live sync: AML hash changed {hashBefore:X8} -> {hash:X8}, refreshing viewer.");
                    PushIhToWebView();
                    break;
                case TreeChangeAction.ShowBar:
                    PluginLog.Warn($"[{_ihLabel}] Live sync: AML changed while the diagram had edits that are not applied — asking." +
                                   $" hash-before={hashBefore:X8} hash-now={hash:X8}");
                    UpdateConflictBar();
                    SetStatus("The InstanceHierarchy was changed in the editor. Choose Apply diagram or Reload from AML.");
                    break;
                default:
                    PluginLog.Debug($"[{_ihLabel}] Live sync: AML changed again while the bar stands.");
                    break;
            }
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
            _pluginFindings = Array.Empty<ValidationFinding>();
            RefreshFindings();
            return;
        }
        try
        {
            var options = BuildValidationOptions(_settings);
            _pluginFindings = Vdi3682Validator.ValidateStructured(_doc, options);
            RefreshFindings();

            if (_pluginFindings.Count == 0)
            {
                PluginLog.Debug($"[{_ihLabel}] VDI 3682 validation: clean.");
                return;
            }
            foreach (var f in _pluginFindings)
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

    // ── One list of findings (016f E3, V25) ───────────────────────────────

    private void OnFindingsFromPage(IReadOnlyList<PageFinding> findings, IReadOnlyCollection<string> rules)
    {
        if (_disposed) return;
        _pageFindings = findings;
        _pageRules = new HashSet<string>(rules, StringComparer.Ordinal);
        RefreshFindings();
    }

    /// <summary>The findings of FPB.js and of the plugin's check in one list, each once.</summary>
    private void RefreshFindings()
    {
        if (!_settings.RunVdiValidation)
        {
            UpdateFindingsUi(Array.Empty<FindingRow>());
            return;
        }
        var merged = FindingsMerge.Merge(_pluginFindings, _pageFindings, _pageRules, PlaceOf);
        var rows = merged.Select(m => m.Plugin != null
            ? FindingRow.From(m.Plugin, _doc, LayerOfObject(m.Plugin.TargetIeId))
            : FindingRow.FromPage(m.Page!, LabelOfElement(m.Page!), m.Page!.ProcessId is { } layer ? _index.ProcessName(layer) : null))
            .ToList();
        UpdateFindingsUi(rows);
    }

    private TargetPlace PlaceOf(string target)
    {
        if (_index.Contains(target)) return TargetPlace.InDiagram;
        if (_ih != null && ContainsObject(_ih.InternalElement, target)) return TargetPlace.InThisHierarchy;
        if (_doc != null && CaexToFpbJson.FindFpdInstanceHierarchies(_doc)
                .Any(ih => !string.Equals(ih.ID, _ih?.ID, StringComparison.OrdinalIgnoreCase) && ContainsObject(ih.InternalElement, target)))
            return TargetPlace.OtherFpdHierarchy;
        return TargetPlace.Elsewhere;
    }

    private static bool ContainsObject(IEnumerable<InternalElementType> elements, string bare)
    {
        static bool Same(string? id, string bare) => string.Equals((id ?? "").Trim('{', '}'), bare, StringComparison.OrdinalIgnoreCase);
        foreach (var ie in elements)
        {
            if (Same(ie.ID, bare)) return true;
            if (ie.InternalLink.Any(link => Same(link.ID, bare))) return true;
            if (ContainsObject(ie.InternalElement, bare)) return true;
        }
        return false;
    }

    private string? LayerOfObject(string? objectId) =>
        !string.IsNullOrEmpty(objectId) && _index.TryGetFpb(objectId, out _, out var layer) ? _index.ProcessName(layer) : null;

    private string? LabelOfElement(PageFinding finding)
    {
        if (string.IsNullOrEmpty(finding.ElementId) || string.IsNullOrEmpty(finding.ProcessId)) return null;
        var objectId = _index.IeIdFor(finding.ElementId, finding.ProcessId);
        var found = objectId != null ? FindObject(objectId) : null;
        return string.IsNullOrEmpty(found?.Name) ? null : found!.Name;
    }

    /// <summary>
    /// Mirror the latest findings into the bottom DataGrid + the summary label.
    /// Marshalled to the UI thread because validation may run off-thread later.
    /// </summary>
    private void UpdateFindingsUi(IReadOnlyList<FindingRow> rows)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateFindingsUi(rows));
            return;
        }

        _findings.Clear();
        foreach (var row in rows) _findings.Add(row);

        if (rows.Count == 0)
        {
            FindingsSummary.Text = " — no findings";
        }
        else
        {
            var e = rows.Count(r => r.SeverityRank == 0);
            var w = rows.Count(r => r.SeverityRank == 1);
            var i = rows.Count(r => r.SeverityRank == 2);
            FindingsSummary.Text = $" — {e} error · {w} warning · {i} info";
        }
    }

    /// <summary>
    /// Double-click on a finding shows its element in the diagram, on its layer.
    /// A finding of the plugin goes through the index; one without an element
    /// in the diagram does nothing.
    /// </summary>
    private void FindingsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_disposed || _bridge == null) return;
        if (FindingsGrid.SelectedItem is not FindingRow row) return;
        if (string.IsNullOrEmpty(row.ElementId)) return;
        if (row.FromDiagram)
            _bridge.Select(row.ElementId, string.IsNullOrEmpty(row.ProcessId) ? null : row.ProcessId);
        else if (_index.TryGetFpb(row.ElementId, out var fpbId, out var processId))
            _bridge.Select(fpbId, processId);
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
        // _bridge.Dispose only detaches the CoreWebView2 event handlers; the
        // WebView2 control itself (and its msedgewebview2 child process) lives
        // on until finalisation. Over a long session every doc-switch / New
        // Process / Import spawns a fresh IhView, so without an explicit dispose
        // the browser processes accumulate and drag the editor down. The control
        // is owned by XAML but has already been pulled from the visual tree by
        // the time DisposeAllIhViews clears the tab items.
        try { WebView?.Dispose(); } catch { /* best effort */ }
        _pendingSnapshot = null;
        _pendingSnapshotTimestamp = DateTime.MinValue;
        _doc = null;
        _ih = null;
    }
}
