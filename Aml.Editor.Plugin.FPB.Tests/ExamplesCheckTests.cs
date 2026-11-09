// The examples the plugin ships are free of errors and warnings in the whole
// check: the plugin's own rules and every OCL rule of the mapper. Nothing else
// ran the live rule set over a clean model, so for months the released plugin
// reported G2 (StateHasConcreteType) on every state: the rule iterates over
// type names, which the OCL.NET pushed to GitHub could not evaluate, while every
// local build had the change. Notes stay allowed (D8 before a release).

using Aml.Editor.Plugin.FPB.Validation;
using Aml.Engine.CAEX;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class ExamplesCheckTests
{
    private static string Examples()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "examples");
            if (File.Exists(Path.Combine(path, "Showcase-A-WaermetauscherMitRegelung.aml"))) return path;
        }
        throw new DirectoryNotFoundException("examples/ with the showcases was not found above the test folder.");
    }

    public static IEnumerable<object[]> Showcases() =>
        Directory.EnumerateFiles(Examples(), "Showcase-*.aml").OrderBy(f => f).Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Showcases))]
    public void An_example_passes_the_whole_check_without_errors_or_warnings(string file)
    {
        var doc = CAEXDocument.LoadFromString(File.ReadAllText(Path.Combine(Examples(), file)));

        var problems = Vdi3682Validator.ValidateStructured(doc)
            .Where(f => f.Severity is ValidationSeverity.Error or ValidationSeverity.Warning)
            .Select(f => f.RuleId + " " + f.Severity + " " + f.TargetIeId)
            .ToList();

        Assert.Empty(problems);
    }
}
