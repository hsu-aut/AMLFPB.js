// 031 V8: the OCL rules of the plugin give the same verdict as the model check
// of FPB.js on the corpus FPB.JS/tests/fixtures/model-check (the FPB.js side:
// tests/e2e/model-check-parity.spec.js there). Compared are the rules a case
// names, as pairs of rule and element as the diagram knows them (FpdElementIndex:
// the copy of a boundary state is its state, a process has the id of its
// operator); one finding per element and cause. A finding of a compared rule
// that lies in the hierarchy but on no element of the diagram fails the test
// (final review of 031: the links from outside were skipped that way).

using System.Text.Json;
using Aml.Editor.Plugin.FPB.Bridge;
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

    /// <summary>What a finding of OCL may name in one hierarchy: element and link IDs, the names of links without one.</summary>
    private static HashSet<string> Members(InstanceHierarchyType ih)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(IEnumerable<InternalElementType> elements)
        {
            foreach (var ie in elements)
            {
                members.Add(Bare(ie.ID));
                foreach (var link in ie.InternalLink) members.Add(Bare(link.ID) is { Length: > 0 } id ? id : link.Name ?? "");
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
        var ih = doc.CAEXFile.InstanceHierarchy.First(h => h.Name == expected.hierarchy);
        var members = Members(ih);
        var index = FpdElementIndex.Build(ih);

        var got = new HashSet<string>();
        var unplaced = new List<string>();
        foreach (var f in FindingsMerge.MostSpecific(Vdi3682Validator.ValidateStructured(doc)))
        {
            if (CatalogueCodes.CodeOf(f.RuleId) is not { } code || !expected.rules.Contains(code)) continue;
            var target = Bare(f.TargetIeId);
            if (!members.Contains(target)) continue;                      // another hierarchy of the file
            if (!index.TryGetFpb(target, out var fpbId, out var processId)) { unplaced.Add(code + " " + f.Message); continue; }
            got.Add(code is "A2" or "A3" or "A4" ? Key(code, null, processId) : Key(code, fpbId, null));
        }

        Assert.Empty(unplaced);
        Assert.Equal(expected.findings.Select(i => Key(i.rule, i.element, i.process)).Order(), got.Order());
    }
}
