namespace Aml.Editor.Plugin.FPB.Validation;

/// <summary>A finding of the model check of FPB.js, as the page sends it (016f E3, V25).</summary>
public sealed record PageFinding(string Rule, string Severity, string Message, string? ElementId, string? ProcessId);

/// <summary>Where the object a finding of the plugin points at lies, seen from one tab.</summary>
public enum TargetPlace
{
    /// <summary>An element the diagram of this tab shows.</summary>
    InDiagram,
    /// <summary>In the InstanceHierarchy of this tab, but not in its diagram.</summary>
    InThisHierarchy,
    /// <summary>In the InstanceHierarchy of another tab.</summary>
    OtherFpdHierarchy,
    /// <summary>Outside every FPD InstanceHierarchy, or not found.</summary>
    Elsewhere,
}

/// <summary>One row of the findings list: either a finding of the plugin or one of FPB.js.</summary>
public sealed record MergedFinding(ValidationFinding? Plugin, PageFinding? Page);

/// <summary>
/// The findings list of a tab (016f E3, V25): both checks, each finding once.
/// Every finding of FPB.js stays. For a rule FPB.js checks, its verdict holds
/// at the elements the diagram shows, so the plugin's findings of that rule
/// there go, also when the two disagree. Findings of the plugin at objects the
/// diagram does not show stay, those of another tab's InstanceHierarchy go to
/// that tab, and those without a target or outside every FPD hierarchy stand
/// in every tab.
/// </summary>
public static class FindingsMerge
{
    public static IReadOnlyList<MergedFinding> Merge(
        IReadOnlyList<ValidationFinding> plugin,
        IReadOnlyList<PageFinding> page,
        ISet<string> checkedRules,
        Func<string, TargetPlace> place)
    {
        var merged = new List<MergedFinding>();
        foreach (var finding in plugin)
        {
            var target = Bare(finding.TargetIeId);
            if (target.Length > 0)
            {
                var where = place(target);
                if (where == TargetPlace.OtherFpdHierarchy) continue;
                if (where == TargetPlace.InDiagram
                    && CatalogueCodes.CodeOf(finding.RuleId) is { } code
                    && checkedRules.Contains(code)) continue;
            }
            merged.Add(new MergedFinding(finding, null));
        }
        merged.AddRange(page.Select(p => new MergedFinding(null, p)));
        return merged;
    }

    private static string Bare(string? id) => (id ?? "").Trim().TrimStart('{').TrimEnd('}');
}
