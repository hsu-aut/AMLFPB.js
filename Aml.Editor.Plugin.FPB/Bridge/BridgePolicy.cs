using Aml.Editor.Plugin.FPB.Localization;
using System.IO;

namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>What becomes of a navigation of the plugin page (016f E3, V14).</summary>
public enum NavigationAction
{
    /// <summary>The plugin page itself (its virtual host).</summary>
    Allow,
    /// <summary>A link to the web: opens in the default browser, the page stays.</summary>
    External,
    /// <summary>Anything else (files, data, script): refused and logged.</summary>
    Block,
}

/// <summary>
/// The rules that keep the WebView on its own page (016f E3, V13 to V15):
/// only the virtual host of the plugin is shown, links to the web open in the
/// default browser, everything else is refused, and messages count only from
/// the plugin page. Whatever sends a message writes into the editor's
/// document, so nothing else may talk to the plugin.
/// </summary>
public static class BridgePolicy
{
    public const string Origin = "https://fpbjs.local";
    private static readonly Uri OriginUri = new(Origin + "/");

    public static NavigationAction Classify(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return NavigationAction.Block;
        if (IsOwn(parsed)) return NavigationAction.Allow;
        return parsed.Scheme is "http" or "https" or "mailto" ? NavigationAction.External : NavigationAction.Block;
    }

    /// <summary>
    /// The address of the plugin page; it tells the page the language of the
    /// plugin, which the page passes to FPB.js (016h F2).
    /// </summary>
    public static string PageUrl(bool german) => Origin + "/index.html?lang=" + (german ? "de" : "en");

    /// <summary>True for a message of the plugin page (CoreWebView2WebMessageReceivedEventArgs.Source).</summary>
    public static bool IsOwnSource(string? source) =>
        !string.IsNullOrEmpty(source) && Uri.TryCreate(source, UriKind.Absolute, out var parsed) && IsOwn(parsed);

    private static bool IsOwn(Uri uri) =>
        string.Equals(uri.Scheme, OriginUri.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, OriginUri.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == OriginUri.Port;

    /// <summary>
    /// The WebView2 profile (V13). Without a folder of its own WebView2 writes
    /// next to the editor, which fails under Program Files.
    /// </summary>
    public static string UserDataFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLFPB.js", "WebView2");

    /// <summary>The filter of the save dialog for a download, after its extension (V21).</summary>
    public static string SaveFilterFor(string fileName)
    {
        var all = L.T("All files") + " (*.*)|*.*";
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension)) return all;
        var name = extension.TrimStart('.').ToUpperInvariant();
        return L.T("{0} file", name) + $" (*{extension})|*{extension}|{all}";
    }
}
