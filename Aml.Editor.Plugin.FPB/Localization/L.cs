// The plugin in English or in German, as Windows' display language says
// (AMLFPB_LANGUAGE=en or de overrides it, for screenshots and support), the
// same way the OPC UA plugin decides (016h F2, V10). The English text is the
// key; a text without a German entry stays English, and a test sees to it
// that none is without one. Messages of the mapper, of the OCL rule file and
// of the log stay English (V12).

using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Markup;

namespace Aml.Editor.Plugin.FPB.Localization;

public static class L
{
    /// <summary>
    /// Whether texts are German: from Windows' display language, unless the
    /// environment variable AMLFPB_LANGUAGE says "en" or "de". Settable for tests.
    /// </summary>
    public static bool German { get; set; } = IsGerman(Environment.GetEnvironmentVariable("AMLFPB_LANGUAGE"), WindowsLanguage());

    /// <summary>German or English, by <paramref name="setting"/> when it is "de" or "en", else by Windows.</summary>
    public static bool IsGerman(string? setting, CultureInfo windows) => setting?.Trim().ToLowerInvariant() switch
    {
        "de" => true,
        "en" => false,
        _ => windows.TwoLetterISOLanguageName == "de",
    };

    /// <summary>
    /// The display language of Windows. Not the UI culture of the process: the
    /// AML Editor sets that to English for everything it runs, plugins included.
    /// </summary>
    public static CultureInfo WindowsLanguage()
    {
        try { return new CultureInfo(GetUserDefaultUILanguage()); }
        catch (CultureNotFoundException) { return CultureInfo.InstalledUICulture; }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>The language WebView2 gets, so its own menus speak the same.</summary>
    public static string Language => German ? "de-DE" : "en-US";

    /// <summary>The language the page passes to FPB.js: "de" or "en".</summary>
    public static string PageLanguage => German ? "de" : "en";

    /// <summary>A text in the plugin's language, with its values put in for {0}, {1} …</summary>
    public static string T(string english, params object?[] args)
    {
        var template = German && De.Texts.TryGetValue(english, out var german) ? german : english;
        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }
}

/// <summary>A text in XAML: Text="{l:T 'Update'}".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
