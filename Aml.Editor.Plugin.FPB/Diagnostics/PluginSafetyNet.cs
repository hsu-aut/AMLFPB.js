using System.Diagnostics;
using System.Windows.Threading;

namespace Aml.Editor.Plugin.FPB.Diagnostics;

/// <summary>
/// A net for exceptions of the plugin that no handler caught (016f E3, T19).
/// An exception that reaches the editor's dispatcher may end the editor and
/// the user's work with it. The net takes only exceptions whose stack passes
/// through the plugin, judged by the assembly of each stack frame's method
/// (not by the text of the trace, whose paths name folders), marks them
/// handled and reports them; everything else stays the editor's business.
/// </summary>
public static class PluginSafetyNet
{
    private static readonly System.Reflection.Assembly Ours = typeof(PluginSafetyNet).Assembly;
    private static readonly Dictionary<Dispatcher, DispatcherUnhandledExceptionEventHandler> Attached = new();

    /// <summary>True when a frame of the exception or of an inner one belongs to the plugin.</summary>
    public static bool IsOurs(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (new StackTrace(current, false).GetFrames().Any(f => f.GetMethod()?.DeclaringType?.Assembly == Ours))
                return true;
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsOurs))
                return true;
        }
        return false;
    }

    public static void Attach(Dispatcher dispatcher, Action<Exception> report)
    {
        if (Attached.ContainsKey(dispatcher)) return;
        DispatcherUnhandledExceptionEventHandler handler = (_, e) =>
        {
            if (!IsOurs(e.Exception)) return;
            e.Handled = true;
            try { report(e.Exception); }
            catch { /* the net must not throw itself */ }
        };
        Attached[dispatcher] = handler;
        dispatcher.UnhandledException += handler;
    }

    public static void Detach(Dispatcher dispatcher)
    {
        if (!Attached.Remove(dispatcher, out var handler)) return;
        dispatcher.UnhandledException -= handler;
    }
}
