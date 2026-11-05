// The plugin in English or in German (016h F2, V10 to V12): the display
// language of Windows decides, AMLFPB_LANGUAGE overrides it, as in the
// OPC UA plugin; texts are looked up by their English.

using System.Globalization;
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
}
