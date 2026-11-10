// The plugin in English or in German (016h F2, V10 to V12): the display
// language of Windows decides, AMLFPB_LANGUAGE overrides it, as in the
// OPC UA plugin; texts are looked up by their English.

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Aml.Editor.Plugin.FPB.Localization;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class LocalizationTests
{
    [Theory]
    [InlineData(null, "de", true)]
    [InlineData(null, "en", false)]
    [InlineData(null, "fr", false)]
    [InlineData("en", "de", false)]
    [InlineData("EN", "de", false)]
    [InlineData("de", "en", true)]
    [InlineData("", "de", true)]
    [InlineData("xx", "de", true)]
    public void AMLFPB_LANGUAGE_overrides_the_language_of_Windows(string? setting, string windows, bool german)
    {
        Assert.Equal(german, L.IsGerman(setting, new CultureInfo(windows)));
    }

    [Fact]
    public void The_language_is_Windows_and_not_what_the_host_process_sets()
    {
        // The AML Editor sets the UI culture of its whole process to English.
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var underEnglish = L.WindowsLanguage().Name;
            CultureInfo.CurrentUICulture = new CultureInfo("fr");
            Assert.Equal(underEnglish, L.WindowsLanguage().Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void A_text_keeps_its_values_in_both_languages()
    {
        var german = L.German;
        try
        {
            L.German = false;
            Assert.Equal("Updating 'P' …", L.T("Updating '{0}' …", "P"));
            Assert.Equal("en-US", L.Language);
            L.German = true;
            Assert.Equal("'P' wird übernommen …", L.T("Updating '{0}' …", "P"));
            Assert.Equal("de-DE", L.Language);
        }
        finally
        {
            L.German = german;
        }
    }

    private static string PluginFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Aml.Editor.Plugin.FPB.sln")))
                return Path.Combine(dir.FullName, "Aml.Editor.Plugin.FPB");
        throw new DirectoryNotFoundException("Aml.Editor.Plugin.FPB.sln was not found above the test folder.");
    }

    private static IEnumerable<string> SourceFiles(params string[] extensions) =>
        Directory.EnumerateFiles(PluginFolder(), "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(f.EndsWith)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                        && !f.EndsWith("De.cs"));

    // XAML writes entities (&amp;, &#x25BE;); the key is the decoded text
    private static string Unxml(string text) => System.Net.WebUtility.HtmlDecode(text.Replace("\\'", "'"));

    private static HashSet<string> Keys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(".cs", ".xaml"))
        {
            var source = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(source, @"\bL\.T\(\s*""((?:[^""\\]|\\.)*)"""))
                keys.Add(Regex.Unescape(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(source, @"\{l:T\s+'((?:[^'\\]|\\.)*)'\s*\}"))
                keys.Add(Unxml(m.Groups[1].Value));
            // the templates of the plugin's own rules (ValidationFinding.Of)
            foreach (Match m in Regex.Matches(source, @"ValidationFinding\.Of\([^,""]+,[^,""]+,\s*""((?:[^""\\]|\\.)*)"""))
                keys.Add(Regex.Unescape(m.Groups[1].Value));
        }
        return keys;
    }

    [Fact]
    public void Every_text_has_a_German_entry_and_none_is_left_over()
    {
        var keys = Keys();

        Assert.True(keys.Count > 50, "too few texts found: " + keys.Count);
        Assert.Empty(keys.Where(k => !De.Texts.ContainsKey(k)).Order());
        Assert.Empty(De.Texts.Keys.Where(k => !keys.Contains(k)).Order());
    }

    [Fact]
    public void No_status_text_goes_past_the_translation()
    {
        var bypassing = SourceFiles(".cs")
            .SelectMany(f => File.ReadAllLines(f).Select(line => (File: Path.GetFileName(f), Line: line.Trim())))
            // LogStatus writes only to the log, which stays English (V12)
            .Where(l => Regex.IsMatch(l.Line, @"\b(SetStatus|ShowStatus|ShowJsErrorBanner|MessageBox\.Show)\(\s*\$?""[^""]"))
            .Select(l => $"{l.File}: {l.Line}");

        Assert.Empty(bypassing);
    }

    [Fact]
    public void No_XAML_text_goes_past_the_translation()
    {
        var bypassing = SourceFiles(".xaml")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\b(Text|Content|ToolTip|Header|Title)=""([^""{]*)""")
                .Select(m => (File: Path.GetFileName(f), Attribute: m.Groups[1].Value, Value: m.Groups[2].Value)))
            // symbols of the icon font (&#xE7BA;) are no text
            .Where(a => Regex.IsMatch(System.Net.WebUtility.HtmlDecode(a.Value), @"[A-Za-zÄÖÜäöü]{2,}"))
            .Select(a => $"{a.File}: {a.Attribute}=\"{a.Value}\"");

        Assert.Empty(bypassing);
    }

    // final review of F2: text as the content of an element, and texts set in
    // code on controls or as the title of a message box, went past the checks
    [Fact]
    public void No_XAML_element_text_goes_past_the_translation()
    {
        var bypassing = SourceFiles(".xaml")
            .SelectMany(f => Regex.Matches(Regex.Replace(File.ReadAllText(f), @"<!--.*?-->", "", RegexOptions.Singleline), @">\s*([^<{\s][^<]*)<")
                .Select(m => (File: Path.GetFileName(f), Text: m.Groups[1].Value.Trim())))
            .Where(t => Regex.IsMatch(System.Net.WebUtility.HtmlDecode(t.Text), @"[A-Za-zÄÖÜäöü]{2,}"))
            .Select(t => $"{t.File}: {t.Text}");

        Assert.Empty(bypassing);
    }

    [Fact]
    public void No_text_set_in_code_goes_past_the_translation()
    {
        var bypassing = SourceFiles(".cs")
            .SelectMany(f => File.ReadAllLines(f).Select(line => (File: Path.GetFileName(f), Line: line.Trim())))
            .Where(l => Regex.IsMatch(l.Line, @"\.(Text|Content|ToolTip|Header|Title)\s*=\s*\$?""[^""]*[A-Za-zÄÖÜäöü]{2,}")
                        || Regex.IsMatch(l.Line, @"MessageBox\.Show\([^,]+,\s*\$?""[^""]*[A-Za-zÄÖÜäöü]{2,}"))
            .Select(l => $"{l.File}: {l.Line}");

        Assert.Empty(bypassing);
    }

    [Fact]
    public void Every_German_text_keeps_the_placeholders_of_its_key()
    {
        static string Holes(string text) => string.Join(",", Regex.Matches(text, @"\{\d+(:[^}]*)?\}").Select(m => m.Value).Order());
        Assert.Empty(De.Texts.Where(e => Holes(e.Key) != Holes(e.Value)).Select(e => e.Key));
    }

    [Fact]
    public void The_German_writes_no_dash_where_it_would_pause()
    {
        Assert.DoesNotContain(De.Texts.Values, v => Regex.IsMatch(v, " [-–—] ") || v.Contains('—'));
    }
}
