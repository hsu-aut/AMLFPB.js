// The keys of the diagram stay in the diagram (016f E3, V24). WebView2 for WPF
// hands every key with Ctrl or Alt and every key without a character (Delete,
// arrows, Escape) to WPF as PreviewKeyDown and raises a KeyDown from it that
// bubbles to the editor's window, where Ctrl+Z, Ctrl+C, Delete and the like
// are bound. A key handled there never reaches the page. The tests raise the
// event WebView2 raises (its internal WebView2KeyEventArgs) at a view inside
// a window that binds Delete.

using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Aml.Editor.Plugin.FPB.Views;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class DiagramWebViewTests
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

    /// <summary>Delete pressed in <paramref name="view"/>: how often the window's command ran, and whether the browser would be told the key is handled.</summary>
    private static (int Executed, bool Handled) PressDelete(WebView2 view)
    {
        var executed = 0;
        var window = new Window
        {
            Width = 200, Height = 200, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        window.InputBindings.Add(new KeyBinding(ApplicationCommands.Delete, Key.Delete, ModifierKeys.None));
        window.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => executed++));
        window.Content = view;
        window.Show();
        try
        {
            var argsType = typeof(WebView2).Assembly.GetType("Microsoft.Web.WebView2.Wpf.WebView2Base+WebView2KeyEventArgs", throwOnError: true)!;
            var args = (KeyEventArgs)Activator.CreateInstance(argsType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Delete }, null)!;
            args.RoutedEvent = UIElement.PreviewKeyDownEvent;
            view.RaiseEvent(args);
            return (executed, args.Handled);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void TheEditorDoesNotSeeTheKeysOfTheDiagram()
    {
        RunSta(() =>
        {
            var (executed, handled) = PressDelete(new DiagramWebView());
            Assert.Equal(0, executed);
            Assert.False(handled);
        });
    }

    [Fact]
    public void APlainWebViewHandsTheKeysToTheEditor()
    {
        // the counter-check: this is how Delete reaches the editor today
        RunSta(() =>
        {
            var (executed, _) = PressDelete(new WebView2());
            Assert.Equal(1, executed);
        });
    }
}
