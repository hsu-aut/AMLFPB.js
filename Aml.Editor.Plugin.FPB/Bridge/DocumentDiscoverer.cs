// Reflection-based lookup for the editor's currently open CAEXDocument.
//
// The Aml.Editor.Plugin.Contract API fires INotifyAMLDocumentLoad.DocumentLoaded
// once, when the editor opens a file. Plugin views that are constructed AFTER
// that event (re-mount after undock/redock, restart with a session-restored
// document, late install) never receive it and stay stuck on the "no document"
// placeholder until the user clicks something in the tree or re-opens the file.
//
// This helper walks Application.Current.MainWindow.DataContext for a property
// that exposes the active CAEXDocument so the plugin can rebuild its tabs
// without waiting for another DocumentLoaded callback.
//
// Brittle by nature: same caveats as EditorSaver. Every failure is logged and
// returns null — the worst-case behaviour is "user has to re-open the file
// manually", i.e. exactly what we had before this hack.

using System.Reflection;
using System.Windows;
using Aml.Editor.Plugin.FPB.Diagnostics;
using Aml.Engine.CAEX;

namespace Aml.Editor.Plugin.FPB.Bridge;

public static class DocumentDiscoverer
{
    private static readonly string[] CandidatePropertyNames =
    {
        "CurrentDocument",
        "ActiveDocument",
        "Document",
        "CAEXDocument",
        "AMLDocument",
        "OpenedDocument",
        "SelectedDocument",
    };

    /// <summary>
    /// Try to find the CAEXDocument the editor currently has open without
    /// waiting for an INotifyAMLDocumentLoad callback. Returns null if no
    /// document is open, or if the editor's view-model layout has drifted
    /// away from the heuristics here.
    /// </summary>
    public static CAEXDocument? TryFindCurrentDocument()
    {
        try
        {
            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow == null)
            {
                PluginLog.Debug("Document discovery: Application.Current.MainWindow is null.");
                return null;
            }

            var vm = mainWindow.DataContext;
            if (vm == null)
            {
                PluginLog.Debug("Document discovery: MainWindow.DataContext is null.");
                return null;
            }

            var doc = FindCaexDocumentOnObject(vm, depth: 0);
            if (doc != null)
            {
                PluginLog.Debug($"Document discovery: found CAEXDocument on {vm.GetType().Name}.");
            }
            else
            {
                PluginLog.Debug($"Document discovery: no CAEXDocument found on {vm.GetType().FullName}.");
            }
            return doc;
        }
        catch (Exception ex)
        {
            PluginLog.Error("Document discovery threw", ex);
            return null;
        }
    }

    private static CAEXDocument? FindCaexDocumentOnObject(object root, int depth)
    {
        // Bound recursion. Two levels is enough for MainViewModel → DocumentVM →
        // CAEXDocument layouts and prevents accidental infinite traversal.
        if (depth > 2 || root == null) return null;

        var type = root.GetType();

        // Direct hit: any property whose value is a CAEXDocument.
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (typeof(CAEXDocument).IsAssignableFrom(prop.PropertyType))
            {
                try
                {
                    if (prop.GetValue(root) is CAEXDocument doc) return doc;
                }
                catch { /* swallow — getter may have side-conditions */ }
            }
        }

        // Indirect: walk into a property whose NAME suggests a document holder,
        // and look for a CAEXDocument on that.
        foreach (var name in CandidatePropertyNames)
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop == null) continue;
            object? value;
            try { value = prop.GetValue(root); }
            catch { continue; }
            if (value == null) continue;

            if (value is CAEXDocument doc) return doc;

            var nested = FindCaexDocumentOnObject(value, depth + 1);
            if (nested != null) return nested;
        }

        return null;
    }
}
