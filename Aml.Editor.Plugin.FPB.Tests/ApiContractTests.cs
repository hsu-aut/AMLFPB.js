// Plugin-Robustness B.2 — API-Snapshot tests.
//
// Loads Aml.Editor.Plugin.Contract via reflection (the PackageReference uses
// PrivateAssets=all so the test project can't reference it directly). Each
// test asserts a member or contract the plugin relies on still exists — a
// renamed property surfaces here in CI rather than as a silent UX downgrade.

using System.Reflection;
using Aml.Editor.Plugin.FPB;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class ApiContractTests
{
    private static Assembly ContractAssembly
    {
        get
        {
            // Force a touch on the plugin assembly so its dependency closure loads.
            _ = typeof(FpbPlugin).FullName;
            return AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "Aml.Editor.Plugin.Contract");
        }
    }

    [Fact]
    public void INotifyAMLDocumentLoad_HasExpectedMembers()
    {
        var t = ContractAssembly.GetType("Aml.Editor.Plugin.Contracts.INotifyAMLDocumentLoad", throwOnError: true)!;
        Assert.NotNull(t.GetMethod("DocumentLoaded"));
        Assert.NotNull(t.GetMethod("DocumentUnLoaded"));
        Assert.NotNull(t.GetMethod("ApplicationClose"));
        Assert.NotNull(t.GetEvent("IsDocumentLoaded"));
    }

    /// <summary>
    /// ApplicationTheme is an enum that varies by editor version. We assert
    /// only that the plugin's <c>theme.ToString().Contains("Dark", …)</c>
    /// detection has at least one Dark-named and one Light-named candidate to
    /// match against, so the dark/light switch keeps working.
    /// </summary>
    [Fact]
    public void ApplicationTheme_ContainsDarkAndLightCandidates()
    {
        var t = ContractAssembly.GetType("Aml.Editor.Plugin.Contracts.ApplicationTheme", throwOnError: true)!;
        Assert.True(t.IsEnum);
        var names = Enum.GetNames(t);
        Assert.True(names.Length > 0, "ApplicationTheme enum is empty.");
        Assert.Contains(names, n => n.Contains("Dark", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Light", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// FpbPlugin extends PluginViewBase. The base type must still expose the
    /// DisplayName property (used to set the editor's tab label).
    /// </summary>
    [Fact]
    public void FpbPlugin_BaseTypeStillHasDisplayName()
    {
        var pluginBase = typeof(FpbPlugin).BaseType;
        Assert.NotNull(pluginBase);
        Assert.NotNull(pluginBase!.GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance));
    }
}
