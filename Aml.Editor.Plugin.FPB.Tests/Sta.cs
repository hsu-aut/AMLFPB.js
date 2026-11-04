// Runs a test on a thread of its own in the single-threaded apartment WPF
// needs, and shuts its dispatcher down before the thread ends. Without the
// shutdown, windows and WebView2 objects stay hooked to the exited thread and
// the test host crashed now and then (NullReferenceException in
// HwndSubclass.SubclassWndProc, final review of 016f E3).

using System.Windows.Threading;

namespace Aml.Editor.Plugin.FPB.Tests;

internal static class Sta
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("failed on the STA thread", failure);
    }
}
