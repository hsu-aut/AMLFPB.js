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
    // (Pending snapshots inside individual IhViews are NOT preserved across doc
    // switches in v1.5 — a future revision can plumb per-IH cache here.)
    private readonly Dictionary<CAEXDocument, IhView> _ihViews = new();
    private readonly List<IhView> _orderedViews = new();
    private string? _activeTheme;

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
        };
        Unloaded += (_, __) =>
        {
            PluginLog.Debug("Plugin Unloaded — disposing IH views and unsubscribing PluginLog.");
            DisposeAllIhViews();
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

    // ── INotifyAMLDocumentLoad ──────────────────────────────────────────

    public event EventHandler<CAEXDocument>? IsDocumentLoaded;

    public void DocumentLoaded(CAEXDocument document)
    {
        if (document == null) { DocumentUnLoaded(); return; }
        _currentDocument = document;
        if (!ReferenceEquals(document, _lastPushedDocument))
        {
            _lastPushedDocument = document;
            _ = RebuildTabsForDocumentAsync(document);
        }
        IsDocumentLoaded?.Invoke(this, document);
    }

    public void DocumentUnLoaded()
    {
        DisposeAllIhViews();
        _currentDocument = null;
        _currentFilePath = null;
        _lastPushedDocument = null;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public void ApplicationClose()
    {
        DisposeAllIhViews();
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
        DisposeAllIhViews();

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
                if (!string.IsNullOrEmpty(_activeTheme)) view.SendTheme(_activeTheme);
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Failed to initialise IhView '{label}'", ex);
            }
        }

        if (IhTabs.Items.Count > 0) IhTabs.SelectedIndex = 0;
        PluginLog.Info($"Document opened: {ihs.Count} FPD InstanceHierarchy(ies) → {ihs.Count} viewer tab(s).");
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

            FpbJsonToCaex.CreateEmptyFpdInstanceHierarchy(_currentDocument, ihName, processName);
            LogStatus($"Created empty InstanceHierarchy '{ihName}' with process '{processName}'. Press Ctrl+S to persist.");

            _ = RebuildTabsForDocumentAsync(_currentDocument);
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
                // Rebuild tabs so the freshly-added IH gets its own sub-tab.
                _ = RebuildTabsForDocumentAsync(_currentDocument);
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
