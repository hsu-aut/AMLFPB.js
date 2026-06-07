// Last-resort reflection bridge into the AMLEditor's internal SaveAMLCommand.
//
// The Aml.Editor.Plugin.Contract API exposes Document-Load/Unload/Selection
// callbacks but no "save the current AML file". The editor's own toolbar binds
// to a SaveAMLCommand on its main view-model — internal, but reachable via
// Application.Current.MainWindow.DataContext.
//
// This is brittle: a future editor version may rename the ViewModel or the
// command. Every call is wrapped in try/catch and the worst-case behaviour is
// "nothing happens, user has to press Ctrl+S themselves" — same as before this
// hack existed. Failure modes are logged via PluginLog so we can spot drift.

using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Aml.Editor.Plugin.FPB.Diagnostics;

namespace Aml.Editor.Plugin.FPB.Bridge;

public static class EditorSaver
{
    private static readonly string[] CandidateProperties =
    {
        "SaveAMLCommand",
        "SaveCommand",
        "SaveActiveDocumentCommand",
        "SaveCurrentAMLFileCommand",
    };

    /// <summary>
    /// Try to trigger the editor's own save action without going through the
    /// filesystem ourselves. Returns true if a command was found AND executed.
    /// </summary>
    public static bool TrySaveActiveDocument()
    {
        try
        {
            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow == null)
            {
                PluginLog.Debug("Editor save reflection: Application.Current.MainWindow is null.");
                return false;
            }

            var vm = mainWindow.DataContext;
            if (vm == null)
            {
                PluginLog.Debug("Editor save reflection: MainWindow.DataContext is null.");
                return false;
            }

            var vmType = vm.GetType();
            foreach (var name in CandidateProperties)
            {
                var prop = vmType.GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (prop == null) continue;

                if (prop.GetValue(vm) is not ICommand cmd)
                {
                    PluginLog.Debug($"Editor save reflection: {vmType.Name}.{name} is not ICommand.");
                    continue;
                }

                if (!cmd.CanExecute(null))
                {
                    PluginLog.Debug($"Editor save reflection: {vmType.Name}.{name}.CanExecute(null) is false.");
                    return false;
                }

                // Execute on the UI thread so WPF command-handler invariants hold.
                if (Application.Current.Dispatcher.CheckAccess())
                    cmd.Execute(null);
                else
                    Application.Current.Dispatcher.Invoke(() => cmd.Execute(null));

                // P0 #1: we don't know whether the editor actually persisted —
                // CanExecute=true does not imply IsDirty. Report the command was
                // invoked, not "save completed".
                PluginLog.Info($"Editor save command invoked via {vmType.Name}.{name} " +
                               "(editor may no-op if document was already clean).");
                return true;
            }

            PluginLog.Warn($"Editor save reflection: no matching command on {vmType.FullName}. " +
                           "The editor's internal ViewModel may have been renamed.");
            return false;
        }
        catch (Exception ex)
        {
            PluginLog.Error("Editor save reflection threw", ex);
            return false;
        }
    }
}
