// The package on nuget.org declares no dependencies. The PlugIn Manager of the
// AutomationML Editor installs every declared dependency from nuget.org next to
// the plugin, which hung the install in September 2026. Aml.Engine, the editor
// API and the skins come from the editor at run time, the rest is packed into
// lib/. So every reference of the plugin project keeps its assets private.

using System.Xml.Linq;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class PackageTests
{
    private static XDocument PluginProject()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "Aml.Editor.Plugin.FPB", "Aml.Editor.Plugin.FPB.csproj");
            if (File.Exists(path)) return XDocument.Load(path);
        }
        throw new FileNotFoundException("Aml.Editor.Plugin.FPB.csproj was not found above the test folder.");
    }

    private static string? PrivateAssets(XElement reference) =>
        (string?)reference.Attribute("PrivateAssets") ?? reference.Elements("PrivateAssets").Select(e => e.Value).FirstOrDefault();

    [Fact]
    public void Every_reference_of_the_plugin_keeps_its_assets_private()
    {
        var references = PluginProject().Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference")
            .ToList();
        Assert.NotEmpty(references);

        var declared = references
            .Where(r => !string.Equals(PrivateAssets(r)?.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            .Select(r => (string?)r.Attribute("Include"))
            .ToList();

        Assert.Empty(declared);
    }

    [Fact]
    public void The_package_carries_the_tag_the_editor_searches_for_and_links_its_repository()
    {
        var properties = PluginProject().Descendants()
            .Where(e => !e.HasElements)
            .GroupBy(e => e.Name.LocalName)
            .ToDictionary(g => g.Key, g => g.First().Value);

        Assert.Contains("AMLEditorPlugin.Net", properties["PackageTags"]);
        Assert.Equal("https://github.com/hsu-aut/AMLFPB.js", properties.GetValueOrDefault("PackageProjectUrl"));
        Assert.Equal("https://github.com/hsu-aut/AMLFPB.js", properties.GetValueOrDefault("RepositoryUrl"));
        Assert.Equal("README.md", properties.GetValueOrDefault("PackageReadmeFile"));
    }
}
