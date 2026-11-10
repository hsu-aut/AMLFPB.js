// The tab view must build: a property of the WebView set in XAML that throws
// on load leaves the plugin empty in the editor (found in the real editor,
// 016f E3 Task 12).

using Aml.Editor.Plugin.FPB.Views;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class IhViewTests
{
    [Fact]
    public void TheTabViewBuilds()
    {
        Sta.Run(() =>
        {
            using var view = new IhView();
            Assert.False(view.AcceptsDroppedFiles);
        });
    }

    // run in the real editor (016f E3): in the narrow pane the WPF ToolBar put
    // the status into its overflow, where nobody saw "Applied and saved." or
    // the hint after the editor's save
    [Fact]
    public void TheStatusIsNotInsideTheToolBar()
    {
        Sta.Run(() =>
        {
            using var view = new IhView();
            var status = (System.Windows.FrameworkElement)view.FindName("StatusLabel");
            Assert.NotNull(status);
            for (System.Windows.DependencyObject? o = status; o != null; o = System.Windows.LogicalTreeHelper.GetParent(o))
                Assert.IsNotType<System.Windows.Controls.ToolBar>(o);
        });
    }
}
