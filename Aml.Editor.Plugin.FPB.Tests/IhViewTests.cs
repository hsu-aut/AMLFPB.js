// The tab view must build: a property of the WebView set in XAML that throws
// on load leaves the plugin empty in the editor (found in the real editor,
// 016f E3 Task 12).

using Aml.Editor.Plugin.FPB.Views;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class IhViewTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("failed on the STA thread", failure);
    }

    [Fact]
    public void TheTabViewBuilds()
    {
        RunSta(() =>
        {
            using var view = new IhView();
            Assert.False(view.AcceptsDroppedFiles);
        });
    }
}
