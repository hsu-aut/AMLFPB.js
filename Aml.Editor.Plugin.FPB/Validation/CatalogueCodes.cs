namespace Aml.Editor.Plugin.FPB.Validation;

/// <summary>
/// The id in the VDI 3682 rule catalog (e.g. "D4") of every rule the plugin's
/// check reports, so the findings list can tell which of them FPB.js checks
/// itself (016f E3, V25). The OCL ids follow the comments at
/// <c>Vdi3682OclRuleSet.CatalogueSeverity</c>; a test keeps this table
/// complete.
/// </summary>
public static class CatalogueCodes
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal)
    {
        // the hard-coded rules (Vdi3682Validator.DefaultRules): only what AML has alone (031)
        ["VDI3682.InternalLinkInterface"] = "C6",
        ["VDI3682.RefObjIntegrity"] = "F1",

        // the OCL rule set
        [Oc("ProjectMinimumProcess")] = "A1",
        [Oc("SystemLimitCardinality")] = "A2",
        [Oc("StateMinimumCardinality")] = "A3",
        [Oc("ProcessOperatorMinimumCardinality")] = "A4",
        [Oc("FlowEndpointsTyped")] = "C1",
        [Oc("NoStateToStateFlow")] = "C2",
        [Oc("NoProcessOperatorToProcessOperatorFlow")] = "C3",
        [Oc("UsageEndpointsTyped")] = "C4",
        [Oc("NoDuplicateConnections")] = "C5",
        [Oc("FlowDirected")] = "C6",
        [Oc("NoMixedFlowTypes")] = "C9",
        [Oc("UniqueIdentifiers")] = "D1",
        [Oc("ProcessNamed")] = "D6",
        [Oc("LongNameMandatory")] = "D7",
        [Oc("VersionRevisionPresent")] = "D8",
        [Oc("RefObjResolvable")] = "F1",
        [Oc("RefProcessResolvable")] = "F2",
        [Oc("AllReferencesResolvable")] = "F3",
        [Oc("ProcessOperatorHasIO")] = "G1",
        [Oc("StateHasConcreteType")] = "G2",
        [Oc("NoSelfReference")] = "G3",
        [Oc("NoOrphanedElements")] = "G4",
        [Oc("ProductBalance")] = "G6",
        [Oc("SourceTargetSameProcess")] = "I3",
    };

    // reported without a catalog id: an element no rule can see, a failed OCL pass
    private static readonly string[] WithoutCode =
    {
        Vdi3682OclRuleSet.UnclassifiedRuleId,
        Vdi3682OclRuleSet.RuleIdPrefix.TrimEnd('.'),
    };

    private static string Oc(string invariant) => Vdi3682OclRuleSet.RuleIdPrefix + invariant;

    /// <summary>Every rule id the plugin's check can report.</summary>
    public static IReadOnlyCollection<string> KnownRuleIds { get; } = Codes.Keys.Concat(WithoutCode).ToHashSet(StringComparer.Ordinal);

    /// <summary>The catalog id of a rule of the plugin, or null for one outside the catalog.</summary>
    public static string? CodeOf(string ruleId) => Codes.TryGetValue(ruleId, out var code) ? code : null;
}
