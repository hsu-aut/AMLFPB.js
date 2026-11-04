// Messages of the page that lead to dialogs (Ctrl+S: questions, the editor's
// save) or to the editor's selection run after the WebView2 event, not inside
// it: WebView2 does not support nested message loops in its event handlers
// (final review of 016f E3).

using System.Windows.Threading;
using Aml.Editor.Plugin.FPB.Bridge;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class FpbWebViewTests
{
    private const string Own = "https://fpbjs.local/index.html";

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [Theory]
    [InlineData("{\"type\":\"save\",\"data\":[]}")]
    [InlineData("{\"type\":\"selected\",\"id\":\"a\",\"processId\":\"p\"}")]
    [InlineData("{\"type\":\"findings\",\"rules\":[],\"items\":[]}")]
    public void SaveSelectionAndFindingsRunAfterTheEvent(string json)
    {
        Sta.Run(() =>
        {
            var bridge = new FpbWebView(new WebView2());
            var handled = 0;
            bridge.OnSaveRequested += _ => handled++;
            bridge.OnSelected += (_, _) => handled++;
            bridge.OnFindings += (_, _) => handled++;
            bridge.ReceiveMessage(Own, json);
            Assert.Equal(0, handled);
            Pump();
            Assert.Equal(1, handled);
            bridge.Dispose();
        });
    }

    [Fact]
    public void AMessageOfAForeignSourceIsDropped()
    {
        Sta.Run(() =>
        {
            var bridge = new FpbWebView(new WebView2());
            var handled = 0;
            bridge.OnSaveRequested += _ => handled++;
            bridge.ReceiveMessage("https://evil.example/", "{\"type\":\"save\",\"data\":[]}");
            Pump();
            Assert.Equal(0, handled);
            bridge.Dispose();
        });
    }
}
