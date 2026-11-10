// The rules that keep the WebView on the plugin page (016f E3, V13 to V15).

using Aml.Editor.Plugin.FPB.Bridge;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class BridgePolicyTests
{
    [Theory]
    [InlineData("https://fpbjs.local/index.html", NavigationAction.Allow)]
    [InlineData("https://FPBJS.local/css/fpbjs.css", NavigationAction.Allow)]
    [InlineData("https://fpbjs.local.evil.com/", NavigationAction.External)]
    [InlineData("https://github.com/hsu-aut/x", NavigationAction.External)]
    [InlineData("http://example.org/", NavigationAction.External)]
    [InlineData("mailto:a@b.c", NavigationAction.External)]
    [InlineData("file:///C:/x.aml", NavigationAction.Block)]
    [InlineData("javascript:alert(1)", NavigationAction.Block)]
    [InlineData("data:text/html,x", NavigationAction.Block)]
    [InlineData("about:blank", NavigationAction.Block)]
    [InlineData("", NavigationAction.Block)]
    [InlineData("not a uri", NavigationAction.Block)]
    public void ClassifyKeepsThePageAndSendsLinksToTheBrowser(string uri, NavigationAction expected)
    {
        Assert.Equal(expected, BridgePolicy.Classify(uri));
    }

    [Theory]
    [InlineData("https://fpbjs.local/index.html", true)]
    [InlineData("https://fpbjs.local.evil.com/", false)]
    [InlineData("https://evil.com/?https://fpbjs.local/", false)]
    [InlineData("http://fpbjs.local/index.html", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OnlyThePluginPageIsAnOwnSource(string? source, bool expected)
    {
        Assert.Equal(expected, BridgePolicy.IsOwnSource(source));
    }

    [Fact]
    public void TheProfileLivesUnderLocalAppData()
    {
        var folder = BridgePolicy.UserDataFolder();
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
        Assert.EndsWith(Path.Combine("AMLFPB.js", "WebView2"), folder);
    }

    [Theory]
    [InlineData("Temperieren.svg", "SVG file (*.svg)|*.svg|All files (*.*)|*.*")]
    [InlineData("Temperieren.pdf", "PDF file (*.pdf)|*.pdf|All files (*.*)|*.*")]
    [InlineData("download", "All files (*.*)|*.*")]
    public void TheSaveDialogOffersTheTypeOfTheDownload(string fileName, string expected)
    {
        Assert.Equal(expected, BridgePolicy.SaveFilterFor(fileName));
    }

    // 016h F2: the page learns the language of the plugin from its address
    [Fact]
    public void The_page_learns_the_language_from_its_address()
    {
        Assert.Equal("https://fpbjs.local/index.html?lang=de", BridgePolicy.PageUrl(true));
        Assert.Equal("https://fpbjs.local/index.html?lang=en", BridgePolicy.PageUrl(false));
        Assert.True(BridgePolicy.IsOwnSource(BridgePolicy.PageUrl(true)));
        Assert.Equal(NavigationAction.Allow, BridgePolicy.Classify(BridgePolicy.PageUrl(true)));
    }
}
