using Aml.Editor.Plugin.FPB.Validation;        // hard-coded Vdi3682Validator (+ plugin ValidationFinding)
using Aml.Engine.CAEX;
using FpbMapper.Conversion;                     // FpbJsonToCaex
using OclNet.Caex;                              // CaexMetamodel
using static FpbMapper.Conversion.FpbMappings;  // ElementToSuc
using static FpbMapper.Conversion.FpbTypes;     // Process
using Ocl = OclNet.Core.Validation;             // OclValidator, OclRuleSpec, OclNet ValidationSeverity
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

/// <summary>
/// Acceptance: the published VDI 3682 OCL constraints, run by the OclNet engine,
/// produce findings identical to the hard-coded rules on real mapper output.
///
/// Scoped to the structural cardinality rules (A2/A3/A4) over top-level processes —
/// the hard-coded validator only inspects top-level processes, whereas the OCL
/// engine evaluates every process (incl. sub-processes), so the OCL side is
/// filtered to the same scope for a like-for-like comparison.
/// </summary>
public class SideBySideValidationTests
{
    private static string TestDataPath(string f) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", f);

    private static readonly Ocl.OclRuleSpec[] CardinalityRules =
    {
        new("VDI3682.SystemLimitCardinality", Ocl.ValidationSeverity.Error, "VDI 3682 A2",
            "context FPD_Process inv SystemLimitCardinality: self.containedElement->select(e | e.oclIsKindOf(FPD_SystemLimit))->size() = 1"),
        new("VDI3682.StateCardinality", Ocl.ValidationSeverity.Warning, "VDI 3682 A3",
            "context FPD_Process inv StateMinimumCardinality: self.containedElement->select(e | e.oclIsKindOf(FPD_State))->size() >= 2"),
        new("VDI3682.ProcessOperatorCardinality", Ocl.ValidationSeverity.Warning, "VDI 3682 A4",
            "context FPD_Process inv ProcessOperatorMinimumCardinality: self.containedElement->select(e | e.oclIsKindOf(FPD_ProcessOperator))->size() >= 1"),
    };

    [Fact]
    public void Ocl_engine_matches_hardcoded_validator_on_cardinality()
    {
        var doc = FpbJsonToCaex.Convert(File.ReadAllText(TestDataPath("Temperieren.json"))).Value!;
        var ruleIds = CardinalityRules.Select(r => r.Id).ToHashSet();
        var topLevelProcesses = TopLevelProcessNames(doc);

        var hardcoded = Vdi3682Validator.ValidateStructured(doc)
            .Where(f => ruleIds.Contains(f.RuleId))
            .Select(f => (f.RuleId, Process: ResolveName(doc, f.TargetIeId)))
            .Where(t => topLevelProcesses.Contains(t.Process))
            .ToHashSet();

        var ocl = new Ocl.OclValidator().Validate(new CaexMetamodel(doc), CardinalityRules)
            .Select(f => (f.RuleId, Process: ResolveName(doc, f.TargetId)))
            .Where(t => topLevelProcesses.Contains(t.Process))
            .ToHashSet();

        Assert.Equal(hardcoded, ocl);
    }

    private static HashSet<string> TopLevelProcessNames(CAEXDocument doc)
    {
        var processSuc = ElementToSuc[Process];
        return doc.CAEXFile.InstanceHierarchy
            .SelectMany(ih => ih.InternalElement)
            .Where(ie => ie.RefBaseSystemUnitPath == processSuc)
            .Select(ie => ie.Name ?? "")
            .ToHashSet();
    }

    /// <summary>Resolve a finding target (IE id or name) to the element Name, so both validators compare on the same key.</summary>
    private static string ResolveName(CAEXDocument doc, string? target)
    {
        if (string.IsNullOrEmpty(target)) return "";
        var bare = target.Trim('{', '}');
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
            foreach (var ie in AllIes(ih.InternalElement))
                if ((ie.ID ?? "").Trim('{', '}') == bare || ie.Name == target)
                    return ie.Name ?? bare;
        return target;
    }

    private static IEnumerable<InternalElementType> AllIes(IEnumerable<InternalElementType> roots)
    {
        foreach (var ie in roots)
        {
            yield return ie;
            foreach (var child in AllIes(ie.InternalElement)) yield return child;
        }
    }
}
