// FPB.JS Import/Export Plugin for AutomationML Editor.
//
// v1.5+ architecture: the FPB.JS-Viewer tab hosts a nested TabControl with one
// IhView sub-tab per FPD-bearing InstanceHierarchy in the current document.
// Update / Refresh / Export are scoped to a single IH (the active sub-tab);
// only Import remains on the editor toolbar because it creates a brand-new IH.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.Contracts;
using Aml.Editor.Plugin.WPFBase;
using Aml.Editor.Plugin.FPB.Bridge;
using Aml.Editor.Plugin.FPB.Diagnostics;
using Aml.Editor.Plugin.FPB.Views;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using Microsoft.Win32;

namespace Aml.Editor.Plugin.FPB;

public partial class FpbPlugin : PluginViewBase, IToolBarIntegration, ISupportsThemes, INotifyAMLDocumentLoad
{
    private CAEXDocument? _currentDocument;
    private string? _currentFilePath;
    private CAEXDocument? _lastPushedDocument;

    private PluginSettings _settings = new();
    private const int MaxStatusLogLines = 500;

    // A.5 — per-document state cache survives tab switches between AML files.
    private readonly Dictionary<CAEXDocument, IhView> _ihViews = new();
    private readonly List<IhView> _orderedViews = new();
    private string? _activeTheme;

    /// <summary>Result of the B.3 startup self-test, surfaced in the diagnostics UI.</summary>
    private List<ApiCompatCheck.CheckResult> _compatResults = new();

    /// <summary>
    /// Tracks the most recently rendered doc so multiple back-to-back callbacks
    /// (DocumentLoaded + ChangeSelectedObject) don't trigger redundant rebuilds.
    /// We deliberately do NOT use a semaphore-gate here: if WebView2 init hangs
    /// inside a previous rebuild, the gate never releases and subsequent
    /// rebuilds (e.g. from "+ New Process") wait forever. Without the gate,
    /// the second caller will dedupe via _lastFullyRebuilt instead.
    /// </summary>
    private CAEXDocument? _lastFullyRebuilt;

    // Monotonic rebuild token. RebuildTabsForDocumentInner awaits WebView2 init
    // per IH, and those awaits let a second rebuild (a fast doc-switch, "+ New
    // Process", or a DocumentUnLoaded) interleave on the UI thread. Without a
    // generation check the stale rebuild's continuation resumes after the newer
    // one already cleared the tab strip and adds ghost tabs bound to the wrong /
    // closed document. Each rebuild claims the next token and bails as soon as it
    // sees a newer one; DocumentUnLoaded bumps it to cancel an in-flight rebuild.
    private int _rebuildGeneration;

    // Per-IH pending-snapshot cache, keyed by (OriginID, IH-bare-id) instead
    // of (CAEXDocument, IH-bare-id). The editor occasionally hot-swaps its
    // CAEXDocument wrapper around the same file (see IsAlreadyRebuilt's
    // OriginID-fallback), and a reference-keyed cache would silently drop
    // pending edits on every hot-swap because the new wrapper instance
    // never matched. OriginID survives the swap.
    private readonly Dictionary<(string originId, string ihId), string> _pendingCache = new();

    // OriginID names the authoring TOOL (same GUID in every editor-saved file);
    // suffix the document's own FileName so two showcase files never share a
    // cache identity.
    private static string OriginIdOf(CAEXDocument? doc) =>
        (doc?.CAEXFile?.SourceDocumentInformation?.FirstOrDefault()?.OriginID ?? "")
        + "|" + (doc?.CAEXFile?.FileName ?? "");

    public FpbPlugin()
    {
        InitializeComponent();
        DisplayName = "AMLFPBjs";
        IsReactive = true;

        // Restore persistent settings before logging is wired up — the verbose
        // toggle wants to influence what we log during startup itself.
        _settings = PluginSettings.Load();
        PluginLog.DebugEnabled = _settings.DebugLogging;

        PluginLog.Init();
        PluginLog.OnLine += OnDiagnosticsLine;
        var asmVersion = typeof(FpbPlugin).Assembly.GetName().Version?.ToString(3) ?? "?";
        PluginLog.Info($"Plugin v{asmVersion} constructing. Log file: {PluginLog.FilePath}");
        PluginLog.Debug($"Settings file: {PluginSettings.FilePath} (debug={_settings.DebugLogging})");

        // B.3 startup self-test — log a compact compatibility report so any
        // silent API drift in the editor or Aml.Engine shows up at startup
        // instead of when the affected feature is first triggered.
        try
        {
            _compatResults = ApiCompatCheck.Run();
            foreach (var line in ApiCompatCheck.FormatReport(_compatResults).Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) PluginLog.Info(line.TrimEnd());
        }
        catch (Exception ex)
        {
            PluginLog.Error("ApiCompatCheck threw at startup", ex);
            _compatResults = new List<ApiCompatCheck.CheckResult>();
        }

        // Eager-compile the OCL rule set at startup so the first validation pass
        // does not pay the parse/compile latency — important for the smoothness
        // of the live demo. Failures surface in the startup log next to the
        // ApiCompatCheck banner.
        try
        {
            if (Validation.Vdi3682OclRuleSet.EnsureCompiled())
                PluginLog.Info("OCL rule set: compiled OK at startup.");
            else
                PluginLog.Error("OCL rule set: compile FAILED at startup. "
                    + "The structural validator stays available; the OCL pass will "
                    + "report a single warning until the cause is resolved. "
                    + $"Cause: {Validation.Vdi3682OclRuleSet.CompileError?.Message}");
        }
        catch (Exception ex)
        {
            PluginLog.Error("OCL EnsureCompiled threw at startup", ex);
        }

        ToolBarCommands = new List<PluginCommand>
        {
            // Toolbar contains the two cross-IH actions — actions that produce a
            // brand new IH (i.e. a new sub-tab inside the viewer). Per-IH Update /
            // Refresh / Export live in each viewer sub-tab.
            new PluginCommand
            {
                CommandName = "New Process",
                CommandButtonContent = new TextBlock { Text = "+ New Process", Margin = new Thickness(4, 0, 4, 0) },
                Command = new RelayCommand<object>(p => ExecuteNewProcess(p), p => CanExecuteNewProcess(p)),
                CommandToolTip = "Create an empty FPD InstanceHierarchy in the current AML document (one process, one SystemLimit). Use the FPB.js palette to model.",
                IsCheckable = false,
            },
            new PluginCommand
            {
                CommandName = "Import FPB.js",
                CommandButtonContent = new TextBlock { Text = "Import FPB.js", Margin = new Thickness(4, 0, 4, 0) },
                Command = new RelayCommand<object>(p => ExecuteImport(p), p => CanExecuteImport(p)),
                CommandToolTip = "Load an FPB.js JSON file and add its FPD content as a new InstanceHierarchy.",
                IsCheckable = false,
            },
        };

        Loaded += (_, __) =>
        {
            if (LogFilePathLabel != null)
                LogFilePathLabel.Text = "→ " + PluginLog.FilePath;
            if (DebugToggle != null)               DebugToggle.IsChecked               = PluginLog.DebugEnabled;
            if (AutoSaveToggle != null)            AutoSaveToggle.IsChecked            = _settings.AutoSaveAfterUpdate;
            if (ConfirmLargeUpdatesToggle != null) ConfirmLargeUpdatesToggle.IsChecked = _settings.ConfirmLargeUpdates;
            if (SafetyThresholdInput != null)      SafetyThresholdInput.Text           = _settings.UpdateSafetyThreshold.ToString();

            // B.4 — re-run the compatibility checks now that the main window's
            // DataContext is reliably populated, then refresh the banner. The
            // constructor-time run logs the early state; this is the one whose
            // result the user sees.
            try
            {
                _compatResults = ApiCompatCheck.Run();
                foreach (var line in ApiCompatCheck.FormatReport(_compatResults).Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) PluginLog.Debug(line.TrimEnd());
            }
            catch (Exception ex) { PluginLog.Error("ApiCompatCheck (Loaded) threw", ex); }
            UpdateCompatibilityBanner();

            // Re-subscribe to the diagnostics stream — Unloaded detaches us
            // (and undock/redock fires both Unloaded and Loaded on the same view).
            PluginLog.OnLine -= OnDiagnosticsLine;  // defensive: avoid double-add
            PluginLog.OnLine += OnDiagnosticsLine;

            // Lazy document recovery. Two scenarios that the editor does NOT
            // signal with another DocumentLoaded callback, leaving the plugin
            // stuck on the empty placeholder until the user re-opens the file:
            //   1) Editor restart while a document was open (session restore).
            //   2) Undock + redock of the plugin view, which tears down the
            //      view hierarchy via Unloaded and rebuilds it via Loaded.
            EnsureCurrentDocumentBound();
        };
        Unloaded += (_, __) =>
        {
            // WPF fires Loaded/Unloaded on every visual-tree reparent — that
            // includes when the editor switches its own active tab or just
            // resizes panels. Disposing the IhViews here was triggering an
            // endless rebuild cycle (Unloaded → DisposeAll → Loaded → discover
            // → Rebuild → state accumulates in FPB.JS). Real teardown happens
            // in ApplicationClose / DocumentUnLoaded, so leave the IH views
            // alone here; just detach the diagnostics subscription.
            PluginLog.Debug("Plugin Unloaded — keeping IH views (real teardown is in ApplicationClose/DocumentUnLoaded).");
            PluginLog.OnLine -= OnDiagnosticsLine;
        };
    }

    // ── Plugin identity ─────────────────────────────────────────────────

    public override string PackageName => "Aml.Editor.Plugin.FPB";
    public override DockPositionEnum InitialDockPosition => DockPositionEnum.DockContent;
    public override bool CanClose => true;

    public List<PluginCommand> ToolBarCommands { get; }

    // ── Editor callbacks ────────────────────────────────────────────────

    public override void ChangeAMLFilePath(string amlFilePath)
    {
        base.ChangeAMLFilePath(amlFilePath);
        _currentFilePath = amlFilePath;
    }

    public override void ChangeSelectedObject(CAEXBasicObject selectedObject)
    {
        base.ChangeSelectedObject(selectedObject);
        var doc = selectedObject?.CAEXDocument;
        PluginLog.Debug($"ChangeSelectedObject: doc={DescribeDoc(doc)} lastPushed={DescribeDoc(_lastPushedDocument)} sameRef={ReferenceEquals(doc, _lastPushedDocument)}");

        if (doc != null && !ReferenceEquals(doc, _lastPushedDocument))
        {
            _currentDocument = doc;
            _lastPushedDocument = doc;
            _ = RebuildTabsForDocumentAsync(doc);
        }
        else if (doc != null)
        {
            _currentDocument = doc;
        }
        // Selection cleared (doc == null) leaves _currentDocument alone — see audit P0 #4.
    }

    private static string DescribeDoc(CAEXDocument? doc)
    {
        if (doc == null) return "<null>";
        var hash = doc.GetHashCode().ToString("X8");
        var source = doc.CAEXFile?.SourceDocumentInformation?.FirstOrDefault();
        var origin = source?.OriginID ?? "<no-origin>";
        return $"hash={hash},origin={origin}";
    }

    /// <summary>
    /// Make sure the viewer is wired up to whatever document the editor has
    /// open right now, even when no INotifyAMLDocumentLoad callback fires.
    /// Called on every Loaded, so safe to invoke when nothing needs doing.
    ///
    /// Deferred to Dispatcher background priority so a doc-open in progress
    /// finishes loading first — otherwise reflecting onto MainWindow.DataContext
    /// while the editor is mid-load can crash Aml.Engine (it isn't thread-safe).
    /// </summary>
    private void EnsureCurrentDocumentBound()
    {
        // Case A: we still hold a document reference but Unloaded tore the IH
        // views down. Rebuild against the same document we already know.
        if (_currentDocument != null && _ihViews.Count == 0)
        {
            PluginLog.Debug("Loaded: existing document but no IH tabs — rebuilding.");
            _lastPushedDocument = null;  // force RebuildTabsForDocumentAsync to act
            _lastFullyRebuilt = null;
            _ = RebuildTabsForDocumentAsync(_currentDocument);
            return;
        }

        // Case B: cold start (editor restart, fresh plugin construction) but
        // the editor already has a document open. The DocumentLoaded event
        // fired before our subscription existed; ask the editor's own
        // view-model via reflection. Defer to background priority so the
        // editor finishes any in-flight doc load before we poke its viewmodel.
        if (_currentDocument == null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_currentDocument != null) return; // DocumentLoaded raced in — abort discovery.
                var doc = DocumentDiscoverer.TryFindCurrentDocument();
                if (doc == null) return;
                if (_currentDocument != null) return; // double-check after the async reflection call.

                PluginLog.Info("Discovered an already-open AML document on mount — binding viewer.");
                _currentDocument = doc;
                _lastPushedDocument = doc;
                _ = RebuildTabsForDocumentAsync(doc);
            }), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
    }

    // ── INotifyAMLDocumentLoad ──────────────────────────────────────────

    public event EventHandler<CAEXDocument>? IsDocumentLoaded;

    public void DocumentLoaded(CAEXDocument document)
    {
        PluginLog.Debug($"DocumentLoaded: doc={DescribeDoc(document)} lastPushed={DescribeDoc(_lastPushedDocument)} sameRef={ReferenceEquals(document, _lastPushedDocument)}");
        if (document == null) { DocumentUnLoaded(); return; }
        _currentDocument = document;
        if (!ReferenceEquals(document, _lastPushedDocument))
        {
            _lastPushedDocument = document;
            _ = RebuildTabsForDocumentAsync(document);
        }
        // DO NOT invoke IsDocumentLoaded here — the editor subscribes to that
        // event and reacts by calling DocumentLoaded() again, which would feed
        // itself in an infinite loop (40 calls/sec observed in v0.6.4). The
        // event is part of the INotifyAMLDocumentLoad contract for OTHER
        // subscribers (e.g. nested plugins), not for the editor itself.
    }

    public void DocumentUnLoaded()
    {
        // Document is closing for real — drop any pending-snapshot cache entries
        // tied to it. (Undock/redock goes through DisposeAllIhViews directly and
        // KEEPS the cache, so this method must only nuke truly-closed documents.)
        if (_currentDocument != null)
        {
            var originId = OriginIdOf(_currentDocument);
            var toRemove = _pendingCache.Keys.Where(k => k.originId == originId).ToList();
            foreach (var k in toRemove) _pendingCache.Remove(k);
        }

        // Cancel any rebuild still awaiting WebView2 init: bump the token so its
        // continuation bails instead of adding tabs for a now-closed document.
        _rebuildGeneration++;
        DisposeAllIhViews();
        _currentDocument = null;
        _currentFilePath = null;
        _lastPushedDocument = null;
        _lastFullyRebuilt = null;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public void ApplicationClose()
    {
        DisposeAllIhViews();
        _pendingCache.Clear();
        try { PluginLog.Shutdown(); } catch { /* swallow */ }
    }

    // ── ISupportsThemes ─────────────────────────────────────────────────

    public void OnThemeChanged(ApplicationTheme theme)
    {
        _activeTheme = theme.ToString().Contains("Dark", StringComparison.OrdinalIgnoreCase)
            ? "dark" : "light";
        foreach (var view in _orderedViews)
        {
            try { view.SendTheme(_activeTheme); }
            catch (Exception ex) { PluginLog.Error("Theme push failed", ex); }
        }
    }

    // ── Multi-tab orchestration ─────────────────────────────────────────

    private async Task RebuildTabsForDocumentAsync(CAEXDocument doc)
    {
        // Dedup by reference AND by SourceDocumentInformation OriginID because
        // the editor occasionally hands the plugin two different CAEXDocument
        // wrappers around the same underlying XML.
        if (IsAlreadyRebuilt(doc))
        {
            PluginLog.Debug($"Rebuild skipped — already rendered: {DescribeDoc(doc)}");
            return;
        }
        // Optimistically claim ownership BEFORE running so a parallel callback
        // dedups against us. If the rebuild throws we clear the marker again
        // AND log the exception so it's visible — fire-and-forget callers
        // (DocumentLoaded, ChangeSelectedObject, ExecuteNewProcess) discard
        // the returned Task with `_ =`, which would otherwise swallow the
        // exception entirely (audit-fix #5).
        _lastFullyRebuilt = doc;
        PluginLog.Debug($"RebuildTabsForDocument running for {DescribeDoc(doc)}");
        try
        {
            await RebuildTabsForDocumentInner(doc);
        }
        catch (Exception ex)
        {
            _lastFullyRebuilt = null;
            PluginLog.Error($"RebuildTabsForDocument failed for {DescribeDoc(doc)}", ex);
            try
            {
                if (NoIhPlaceholder != null) NoIhPlaceholder.Visibility = Visibility.Visible;
            }
            catch { /* UI may be unloaded */ }
            // Deliberately NOT rethrowing — the caller is fire-and-forget by
            // design; rethrowing only feeds the unobserved-exception path.
        }
    }

    /// <summary>
    /// Reference-or-origin compare. CAEXDocument wrappers around the same
    /// file aren't always reference-equal; falling back to OriginID closes
    /// the dedup loop.
    /// </summary>
    private bool IsAlreadyRebuilt(CAEXDocument doc)
    {
        if (_lastFullyRebuilt == null) return false;
        if (ReferenceEquals(doc, _lastFullyRebuilt)) return true;
        // OriginID identifies the AUTHORING TOOL, not the document — every file
        // the AML editor ever saved carries the same GUID. Origin alone made a
        // second showcase file look "already rebuilt": its tabs never rendered
        // and Update wrote into the wrong document. Require the document's own
        // FileName to match too.
        var docOrigin = doc.CAEXFile?.SourceDocumentInformation?.FirstOrDefault()?.OriginID;
        var lastOrigin = _lastFullyRebuilt.CAEXFile?.SourceDocumentInformation?.FirstOrDefault()?.OriginID;
        if (string.IsNullOrEmpty(docOrigin) || !string.Equals(docOrigin, lastOrigin, StringComparison.Ordinal))
            return false;
        var docName = doc.CAEXFile?.FileName;
        var lastName = _lastFullyRebuilt.CAEXFile?.FileName;
        return !string.IsNullOrEmpty(docName)
            && string.Equals(docName, lastName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RebuildTabsForDocumentInner(CAEXDocument doc)
    {
        // Claim this rebuild's token AFTER the synchronous teardown below, so our
        // own DisposeAllIhViews (which does not touch the token) never trips our
        // own generation check. Any rebuild or DocumentUnLoaded that starts later
        // bumps the token and makes this run bail at its next checkpoint.
        // Capture pending snapshots from the views we're about to tear down so
        // unsaved edits survive an undock/redock or doc-switch round trip.
        CapturePendingSnapshotsToCache();

        DisposeAllIhViews();

        var myGen = ++_rebuildGeneration;

        var ihs = CaexToFpbJson.FindFpdInstanceHierarchies(doc).ToList();
        if (ihs.Count == 0)
        {
            PluginLog.Info("No FPD InstanceHierarchy in this document — viewer remains empty.");
            NoIhPlaceholder.Visibility = Visibility.Visible;
            return;
        }
        NoIhPlaceholder.Visibility = Visibility.Collapsed;

        int fallbackIndex = 1;
        foreach (var ih in ihs)
        {
            // A newer rebuild (or a DocumentUnLoaded) superseded us while we were
            // awaiting a previous IH's WebView2 init — stop before adding a tab
            // that would belong to the wrong document.
            if (myGen != _rebuildGeneration)
            {
                PluginLog.Debug($"Rebuild superseded (gen {myGen} < {_rebuildGeneration}) — aborting stale tab build.");
                return;
            }

            var label = !string.IsNullOrWhiteSpace(ih.Name) ? ih.Name : $"InstanceHierarchy {fallbackIndex++}";

            var view = new IhView();
            var tab = new TabItem
            {
                Header = label,
                Content = view,
                FontSize = 12,  // match outer-tab styling override in XAML
            };
            view.PendingChanged += (v, hasPending) => UpdateTabHeader(tab, v.IhLabel, hasPending);
            // Tab title follows IH.Name changes the user makes in the AML tree —
            // IhView raises LabelChanged after each Convert push when it notices drift.
            view.LabelChanged += (v, newLabel) => UpdateTabHeader(tab, newLabel, v.HasPendingSnapshot);

            IhTabs.Items.Add(tab);
            _orderedViews.Add(view);
            _ihViews[doc] = view; // last one for this doc — primarily for theme push lookup

            try
            {
                await view.BindAsync(doc, ih, label, _settings);

                // BindAsync awaited WebView2 init — re-check we weren't superseded
                // while it ran. If so, dispose the view we just built (its browser
                // process is live) and bail; DisposeAllIhViews already ran for the
                // newer rebuild, so this orphan view is not in _orderedViews-of-record.
                if (myGen != _rebuildGeneration)
                {
                    PluginLog.Debug($"Rebuild superseded (gen {myGen} < {_rebuildGeneration}) during BindAsync — discarding '{label}'.");
                    try { view.Dispose(); } catch { /* best effort */ }
                    return;
                }

                if (!string.IsNullOrEmpty(_activeTheme)) view.SendTheme(_activeTheme);

                // Restore any cached pending snapshot for this (OriginID, IH) pair.
                var key = (OriginIdOf(doc), NormalizeIhId(ih.ID));
                if (_pendingCache.Remove(key, out var snapshot))
                {
                    view.RestorePendingSnapshot(snapshot);
                    PluginLog.Info($"[{label}] Restored pending edits cached from before the tab rebuild.");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Failed to initialise IhView '{label}'", ex);
            }
        }

        if (myGen != _rebuildGeneration) return;
        if (IhTabs.Items.Count > 0) IhTabs.SelectedIndex = 0;
        PluginLog.Info($"Document opened: {ihs.Count} FPD InstanceHierarchy(ies) → {ihs.Count} viewer tab(s).");
    }

    private void CapturePendingSnapshotsToCache()
    {
        foreach (var view in _orderedViews)
        {
            try
            {
                var snap = view.PeekPendingSnapshot();
                if (string.IsNullOrEmpty(snap)) continue;
                if (view.Ih == null) continue;
                var doc = view.Ih.CAEXDocument;
                if (doc == null) continue;
                _pendingCache[(OriginIdOf(doc), NormalizeIhId(view.Ih.ID))] = snap;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Failed to capture pending snapshot to cache", ex);
            }
        }
    }

    private static string NormalizeIhId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        if (id.Length >= 2 && id[0] == '{' && id[^1] == '}') return id.Substring(1, id.Length - 2);
        return id;
    }

    private static void UpdateTabHeader(TabItem tab, string label, bool hasPending)
    {
        // Keep the header as a plain string so the editor's Aml.Skins TabItem
        // template applies its native styling (background, padding, fonts).
        // Pending state is signalled by a leading bullet glyph in the same colour.
        tab.Header = hasPending ? "● " + label : label;
    }

    private void DisposeAllIhViews()
    {
        foreach (var view in _orderedViews)
        {
            try { view.Dispose(); } catch (Exception ex) { PluginLog.Error("IhView dispose failed", ex); }
        }
        _orderedViews.Clear();
        _ihViews.Clear();
        IhTabs?.Items.Clear();
        if (NoIhPlaceholder != null) NoIhPlaceholder.Visibility = Visibility.Visible;
    }

    // ── New Process command (creates a fresh empty IH → new sub-tab) ────

    private bool CanExecuteNewProcess(object? parameter) => _currentDocument != null;

    private void ExecuteNewProcess(object? parameter)
    {
        if (_currentDocument == null)
        {
            MessageBox.Show("Open an AML document first — the new InstanceHierarchy will be added to it.",
                "FPB.js — New Process", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var dialog = new InputDialog("New FPD Process",
                "Process name:",
                "NewProcess");
            if (dialog.ShowDialog() != true) return;

            var processName = string.IsNullOrWhiteSpace(dialog.Result) ? "NewProcess" : dialog.Result.Trim();
            var ihName = processName.Replace(" ", "_") + "_IH";

            var newIh = FpbJsonToCaex.CreateEmptyFpdInstanceHierarchy(_currentDocument, ihName, processName);
            LogStatus($"Created empty InstanceHierarchy '{ihName}' with process '{processName}'. Press Ctrl+S to persist.");

            // The editor sometimes hot-swaps its CAEXDocument wrapper after our
            // in-memory mutation, leaving us with a stale `_currentDocument`
            // reference and the rebuild rendered against a doc that no longer
            // contains the new IH. Anchor on the IH's OWN document so we
            // always rebuild against the wrapper that actually carries the
            // mutation.
            var docForRebuild = newIh.CAEXDocument ?? _currentDocument;
            _currentDocument = docForRebuild;
            _lastPushedDocument = docForRebuild;

            var fpdIhList = CaexToFpbJson.FindFpdInstanceHierarchies(docForRebuild);
            PluginLog.Debug($"After CreateEmpty '{ihName}': fpdIHs={fpdIhList.Count} names: {string.Join(", ", fpdIhList.Select(ih => $"'{ih.Name}'"))}");

            // Force rebuild even if the editor's reload already triggered one
            // against the (stale) wrapper — clearing the dedup key picks up
            // the new IH regardless of who got there first.
            _lastFullyRebuilt = null;
            _ = RebuildTabsForDocumentAsync(docForRebuild);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            PluginLog.Error("New Process failed", ex);
            MessageBox.Show($"Could not create the new InstanceHierarchy:\n\n{ex.Message}",
                "FPB.js — New Process Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Import command (creates a new IH → new sub-tab) ─────────────────

    private bool CanExecuteImport(object? parameter) => true;

    private void ExecuteImport(object? parameter)
    {
        try
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "FPB.js JSON (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
            };
            if (openDialog.ShowDialog() != true) return;

            var jsonString = File.ReadAllText(openDialog.FileName, System.Text.Encoding.UTF8);

            if (_currentDocument != null)
            {
                var importResult = FpbJsonToCaex.ImportInto(_currentDocument, jsonString);
                LogStatus($"Imported '{Path.GetFileName(openDialog.FileName)}' as a new InstanceHierarchy. " +
                          "Press Ctrl+S in the editor to persist.");
                foreach (var w in importResult.Warnings) PluginLog.Warn(w);
                // Anchor on the importer's returned doc reference so a later
                // editor wrapper-swap doesn't strand the rebuild.
                var docForRebuild = importResult.Value ?? _currentDocument;
                _currentDocument = docForRebuild;
                _lastPushedDocument = docForRebuild;
                _lastFullyRebuilt = null;
                _ = RebuildTabsForDocumentAsync(docForRebuild);
            }
            else
            {
                var convertResult = FpbJsonToCaex.Convert(jsonString);
                var saveDialog = new SaveFileDialog
                {
                    Filter = "AutomationML (*.aml)|*.aml|All files (*.*)|*.*",
                    DefaultExt = ".aml",
                    FileName = Path.GetFileNameWithoutExtension(openDialog.FileName) + ".aml",
                };
                if (saveDialog.ShowDialog() != true) return;
                convertResult.Value.SaveToFile(saveDialog.FileName, true);
                LogStatus($"Imported into new file: {saveDialog.FileName}. Open it in the editor to view.");
                foreach (var w in convertResult.Warnings) PluginLog.Warn(w);
            }
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            PluginLog.Error("Import failed", ex);
            MessageBox.Show($"Import failed:\n\n{ex.Message}", "FPB.js Import Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Status logging ──────────────────────────────────────────────────

    private void LogStatus(string message) => PluginLog.Info(message);

    private void OnDiagnosticsLine(string line)
    {
        if (StatusLog == null) return;
        try
        {
            if (Dispatcher.CheckAccess()) AppendCapped(line);
            else Dispatcher.BeginInvoke(new Action(() => AppendCapped(line)));
        }
        catch { /* during teardown the textbox may be gone */ }
    }

    private void AppendCapped(string line)
    {
        var current = StatusLog.Text ?? string.Empty;
        int newlines = 0, idx = -1;
        while ((idx = current.IndexOf('\n', idx + 1)) >= 0)
        {
            newlines++;
            if (newlines >= MaxStatusLogLines) break;
        }
        if (newlines >= MaxStatusLogLines)
        {
            int cut = 0, seen = 0;
            for (int i = 0; i < current.Length && seen < MaxStatusLogLines - 1; i++)
            {
                if (current[i] == '\n') { seen++; cut = i + 1; }
            }
            current = current.Substring(0, cut);
        }
        StatusLog.Text = line + "\n" + current;
    }

    // ── Diagnostics controls ────────────────────────────────────────────

    private void DebugToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (DebugToggle == null) return;
        var on = DebugToggle.IsChecked == true;
        PluginLog.DebugEnabled = on;
        if (_settings.DebugLogging != on)
        {
            _settings.DebugLogging = on;
            _settings.Save();
        }
    }

    private void AutoSaveToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoSaveToggle == null) return;
        var on = AutoSaveToggle.IsChecked == true;
        if (_settings.AutoSaveAfterUpdate != on)
        {
            _settings.AutoSaveAfterUpdate = on;
            _settings.Save();
        }
    }

    private void UpdateCompatibilityBanner()
    {
        if (CompatBanner == null || CompatBannerText == null) return;
        if (_compatResults.Count == 0)
        {
            CompatBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var failed = _compatResults.Count(r => !r.Ok);
        if (failed == 0)
        {
            CompatBanner.Visibility = Visibility.Collapsed;
            return;
        }
        CompatBanner.Visibility = Visibility.Visible;
        CompatBannerText.Text = $"⚠ Editor API compatibility: {failed} of {_compatResults.Count} checks failed";
        var failedDetails = string.Join("\n",
            _compatResults.Where(r => !r.Ok).Select(r => $"• {r.Name}: {r.Detail}"));
        CompatBanner.ToolTip = "Startup self-test results:\n" + failedDetails +
            "\n\nPlugin falls back to manual workflow where possible. Open the log for full output.";
    }

    private void ConfirmLargeUpdatesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ConfirmLargeUpdatesToggle == null) return;
        var on = ConfirmLargeUpdatesToggle.IsChecked == true;
        if (_settings.ConfirmLargeUpdates != on)
        {
            _settings.ConfirmLargeUpdates = on;
            _settings.Save();
        }
    }

    private void SafetyThresholdInput_LostFocus(object sender, RoutedEventArgs e)
        => CommitSafetyThreshold();

    private void SafetyThresholdInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitSafetyThreshold();
            // Keep focus on a non-textbox so subsequent enter presses don't re-fire.
            ConfirmLargeUpdatesToggle?.Focus();
            e.Handled = true;
        }
    }

    private void CommitSafetyThreshold()
    {
        if (SafetyThresholdInput == null) return;
        if (int.TryParse(SafetyThresholdInput.Text, out var value) && value >= 0)
        {
            if (_settings.UpdateSafetyThreshold != value)
            {
                _settings.UpdateSafetyThreshold = value;
                _settings.Save();
            }
        }
        else
        {
            // Invalid input — revert displayed value to the persisted setting so
            // the user doesn't see e.g. "abc" linger in the box.
            SafetyThresholdInput.Text = _settings.UpdateSafetyThreshold.ToString();
        }
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = PluginLog.LogDirectory;
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            PluginLog.Error("Failed to open log folder", ex);
        }
    }
}
