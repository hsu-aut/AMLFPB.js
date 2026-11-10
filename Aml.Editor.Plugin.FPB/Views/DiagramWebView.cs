using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;

namespace Aml.Editor.Plugin.FPB.Views;

/// <summary>
/// The WebView of a process view, whose keys stay in the diagram (016f E3, V24).
/// <para>
/// WebView2 for WPF hands every key with Ctrl or Alt and every key without a
/// character (Delete, arrows, Escape) to WPF as PreviewKeyDown, and
/// <see cref="WebView2.OnPreviewKeyDown"/> raises a KeyDown from it that
/// bubbles up to the editor's window. The editor binds Ctrl+Z, Ctrl+Y,
/// Ctrl+C, Ctrl+V, Ctrl+S and more there; a key it handles never reaches the
/// page, so Ctrl+Z in the diagram undid in the editor. Leaving out that step
/// keeps the keys away from the editor, and the page gets all of them
/// (e.Handled stays false). Ctrl+S is the page's to handle (V22).
/// </para>
/// </summary>
public sealed class DiagramWebView : WebView2
{
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // no KeyDown to the editor: see the class comment
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // the same for KeyUp
    }
}
