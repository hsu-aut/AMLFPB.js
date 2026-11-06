// 031 V8: the OCL rules of the plugin give the same verdict as the model check
// of FPB.js on the corpus FPB.JS/tests/fixtures/model-check (the FPB.js side:
// tests/e2e/model-check-parity.spec.js there). Compared are the rules a case
// names, as pairs of rule and element: D8 per element, A2 to A4 by the name of
// the process, D1 only whether it is broken; one finding per element and cause.

using System.Text.Json;
using Aml.Editor.Plugin.FPB.Validation;
using Aml.Engine.CAEX;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class ModelCheckParityTests
{
    private static string FpbJs()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "FPB.JS");
            if (Directory.Exists(Path.Combine(path, "tests", "fixtures", "model-check"))) return path;
        }
        throw new DirectoryNotFoundException("FPB.JS/tests/fixtures/model-check was not found above the test folder.");
    }

    public static IEnumerable<object[]> Cases() =>
        Directory.EnumerateFiles(Path.Combine(FpbJs(), "tests", "fixtures", "model-check"), "*.expected.json")
            .Select(f => new object[] { Path.GetFileName(f).Replace(".expected.json", "") })
            .OrderBy(c => (string)c[0]);

    private sealed record Expected(string aml, string hierarchy, string[] rules, Item[] findings);
    private sealed record Item(string rule, string? element, string? process);

    private static string Bare(string? id) => (id ?? "").Trim().TrimStart('{').TrimEnd('}');

    private static string Key(string rule, string? element, string? process) =>
        rule + " " + (process != null ? "process:" + process : element);

    /// <summary>Bare ID to name of every element and link of one hierarchy.</summary>
    private static Dictionary<string, string> Members(InstanceHierarchyType ih)
    {
        var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Walk(IEnumerable<InternalElementType> elements)
        {
            foreach (var ie in elements)
            {
                members.TryAdd(Bare(ie.ID), ie.Name ?? "");
                foreach (var link in ie.InternalLink) members.TryAdd(Bare(link.ID), link.Name ?? "");
                Walk(ie.InternalElement);
            }
        }
        Walk(ih.InternalElement);
        return members;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OCL_gives_the_verdict_of_FPB_js(string name)
    {
        var root = FpbJs();
        var expected = JsonSerializer.Deserialize<Expected>(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "model-check", name + ".expected.json")))!;
        var doc = CAEXDocument.LoadFromFile(Path.Combine(root, expected.aml));
        var members = Members(doc.CAEXFile.InstanceHierarchy.First(ih => ih.Name == expected.hierarchy));

        var got = new HashSet<string>();
        foreach (var f in FindingsMerge.MostSpecific(Vdi3682Validator.ValidateStructured(doc)))
        {
            if (CatalogueCodes.CodeOf(f.RuleId) is not { } code || !expected.rules.Contains(code)) continue;
            if (code == "D1") { got.Add(Key("D1", "*", null)); continue; }
            if (!members.TryGetValue(Bare(f.TargetIeId), out var targetName)) continue;
            got.Add(code is "A2" or "A3" or "A4" ? Key(code, null, targetName) : Key(code, Bare(f.TargetIeId), null));
        }

        Assert.Equal(expected.findings.Select(i => Key(i.rule, i.element, i.process)).Order(), got.Order());
    }
}
