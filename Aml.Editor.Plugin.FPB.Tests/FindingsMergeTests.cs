// One list of findings in a tab (016f E3, V25): the findings of FPB.js and of
// the plugin's check, each once.

using Aml.Editor.Plugin.FPB.Validation;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class FindingsMergeTests
{
    private static readonly ISet<string> FpbChecks = new HashSet<string> { "A2", "B2", "C5", "D7", "G4" };

    // what the tab knows about the target of a finding of the plugin
    private static Func<string, TargetPlace> Places(params (string Id, TargetPlace Place)[] places)
    {
        var map = places.ToDictionary(p => p.Id, p => p.Place);
        return id => map.TryGetValue(id, out var place) ? place : TargetPlace.Elsewhere;
    }

    private static ValidationFinding Plugin(string ruleId, string? target) =>
        new(ruleId, ValidationSeverity.Warning, ruleId + " message", target);

    private static PageFinding Page(string rule, string id) => new(rule, "warning", rule + " of FPB.js", id, "p1");

    private static IReadOnlyList<MergedFinding> Merge(IEnumerable<ValidationFinding> plugin, IEnumerable<PageFinding> page, Func<string, TargetPlace> place) =>
        FindingsMerge.Merge(plugin.ToList(), page.ToList(), FpbChecks, place);

    [Fact]
    public void AllFindingsOfFpbJsStay()
    {
        var merged = Merge(Array.Empty<ValidationFinding>(), new[] { Page("B2", "po1"), Page("D7", "s1") }, Places());
        Assert.Equal(2, merged.Count);
        Assert.All(merged, m => Assert.NotNull(m.Page));
    }

    [Fact]
    public void ARuleFpbJsChecksOnAnElementOfTheDiagramGoes()
    {
        // A2 at a process the diagram shows: FPB.js decides, also when it reports nothing
        var merged = Merge(new[] { Plugin("VDI3682.OCL.SystemLimitCardinality", "proc1") }, Array.Empty<PageFinding>(),
            Places(("proc1", TargetPlace.InDiagram)));
        Assert.Empty(merged);
    }

    [Fact]
    public void ARuleFpbJsChecksOnAnObjectTheDiagramDoesNotShowStays()
    {
        var merged = Merge(new[] { Plugin("VDI3682.OCL.SystemLimitCardinality", "hidden"), Plugin("VDI3682.OCL.UniqueIdentifiers", "hidden") },
            Array.Empty<PageFinding>(), Places(("hidden", TargetPlace.InThisHierarchy)));
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void ARuleFpbJsDoesNotCheckStaysOnAnElementOfTheDiagram()
    {
        var merged = Merge(new[] { Plugin("VDI3682.OCL.UniqueIdentifiers", "s1") }, Array.Empty<PageFinding>(),
            Places(("s1", TargetPlace.InDiagram)));
        Assert.Single(merged);
    }

    [Fact]
    public void AFindingOfAnotherTabGoes()
    {
        var merged = Merge(new[] { Plugin("VDI3682.OCL.UniqueIdentifiers", "other") }, Array.Empty<PageFinding>(),
            Places(("other", TargetPlace.OtherFpdHierarchy)));
        Assert.Empty(merged);
    }

    [Fact]
    public void FindingsElsewhereOrWithoutTargetStay()
    {
        var merged = Merge(new[] { Plugin("VDI3682.OCL.AllReferencesResolvable", "outside"), Plugin("VDI3682.OCL.ProjectMinimumProcess", null) },
            Array.Empty<PageFinding>(), Places(("outside", TargetPlace.Elsewhere)));
        Assert.Equal(2, merged.Count);
        Assert.All(merged, m => Assert.NotNull(m.Plugin));
    }

    [Fact]
    public void TargetsWithBracesAreLookedUpWithout()
    {
        var merged = Merge(new[] { Plugin("VDI3682.OCL.LongNameMandatory", "{s1}") }, Array.Empty<PageFinding>(),
            Places(("s1", TargetPlace.InDiagram)));
        Assert.Empty(merged);
    }
}
