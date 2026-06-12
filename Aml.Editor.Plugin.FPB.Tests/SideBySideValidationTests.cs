using Aml.Editor.Plugin.FPB.Validation;        // hard-coded Vdi3682Validator + Vdi3682OclRuleSet
using Aml.Engine.CAEX;
using FpbMapper.Conversion;                     // FpbJsonToCaex
using OclNet.Caex;                              // CaexMetamodel
using Ocl = OclNet.Core.Validation;             // OclValidator
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

/// <summary>
/// Acceptance: the published VDI 3682 OCL constraints — loaded from the SAME
/// embedded artifacts the plugin runtime uses, not inline copies — produce findings
/// identical to the hard-coded rules, on a model that actually violates them
/// (mutation + NotEmpty guard; an empty-vs-empty comparison proves nothing).
/// Plus: the runtime wiring itself (Vdi3682Validator → OCL pass, toggle off/on).
/// </summary>
public class SideBySideValidationTests
{
    private static string TestDataPath(string f) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", f);

    private static CAEXDocument ConvertTemperieren() =>
        FpbJsonToCaex.Convert(File.ReadAllText(TestDataPath("Temperieren.json"))).Value!;

    /// <summary>Canonical key per rule so both validators' findings compare 1:1.</summary>
    private static readonly Dictionary<string, string> RuleKey = new()
    {
        ["VDI3682.SystemLimitCardinality"] = "A2",
        ["VDI3682.StateCardinality"] = "A3",
        ["VDI3682.ProcessOperatorCardinality"] = "A4",
        [Vdi3682OclRuleSet.RuleIdPrefix + "SystemLimitCardinality"] = "A2",
        [Vdi3682OclRuleSet.RuleIdPrefix + "StateMinimumCardinality"] = "A3",
        [Vdi3682OclRuleSet.RuleIdPrefix + "ProcessOperatorMinimumCardinality"] = "A4",
    };

    private static HashSet<(string Key, string Target)> Normalize(IEnumerable<(string RuleId, string? Target)> findings) =>
        findings
            .Where(f => RuleKey.ContainsKey(f.RuleId))
            .Select(f => (RuleKey[f.RuleId], (f.Target ?? "").Trim('{', '}')))
            .ToHashSet();

    private static HashSet<(string, string)> HardcodedCardinality(CAEXDocument doc) =>
        Normalize(Vdi3682Validator
            .ValidateStructured(doc, new ValidationOptions { UseOclEngine = false })
            .Select(f => (f.RuleId, f.TargetIeId)));

    private static HashSet<(string, string)> OclCardinality(CAEXDocument doc)
    {
        var validator = new Ocl.OclValidator();
        var rules = Vdi3682OclRuleSet.LoadRuleSpecs(includeHardcodedCovered: true);
        var compiled = validator.Compile(rules, Vdi3682OclRuleSet.LoadDefinitions());
        return Normalize(validator.Validate(new CaexMetamodel(doc), compiled).Select(f => (f.RuleId, f.TargetId)));
    }

    // ---- parity --------------------------------------------------------------------

    [Fact]
    public void Valid_model_no_false_positives_on_either_side()
    {
        var doc = ConvertTemperieren();
        Assert.Empty(HardcodedCardinality(doc));
        Assert.Empty(OclCardinality(doc));
    }

    [Fact]
    public void Violated_model_produces_identical_findings()
    {
        var doc = ConvertTemperieren();

        // Mutate: remove the first SystemLimit → its process violates A2.
        var slSuc = FpbMappings.ElementToSuc[FpbTypes.SystemLimit];
        AllIes(doc).First(ie => ie.RefBaseSystemUnitPath == slSuc).Remove();

        var hardcoded = HardcodedCardinality(doc);
        var ocl = OclCardinality(doc);

        Assert.NotEmpty(hardcoded);          // the mutation must actually be detected —
        Assert.Equal(hardcoded, ocl);        // empty == empty would prove nothing
    }

    // ---- runtime wiring --------------------------------------------------------------

    [Fact]
    public void Toggle_off_yields_no_ocl_findings()
    {
        var findings = Vdi3682Validator.ValidateStructured(ConvertTemperieren(),
            new ValidationOptions { UseOclEngine = false });
        Assert.DoesNotContain(findings, f => f.RuleId.StartsWith(Vdi3682OclRuleSet.RuleIdPrefix));
    }

    [Fact]
    public void Runtime_ocl_pass_detects_what_hardcoded_rules_cannot()
    {
        var doc = ConvertTemperieren();

        // Duplicate uniqueIdent — covered ONLY by the OCL catalogue rule D1, no
        // hard-coded rule checks it. The default validator pass must report it.
        var elements = AllIes(doc)
            .Where(ie => ie.Attribute["Identification"]?.Attribute["uniqueIdent"] is not null)
            .Take(2).ToList();
        Assert.Equal(2, elements.Count);
        foreach (var ie in elements)
            ie.Attribute["Identification"]!.Attribute["uniqueIdent"]!.Value = "DUPLICATE-ID";

        var findings = Vdi3682Validator.ValidateStructured(doc);

        Assert.DoesNotContain(findings, f => f.Message.Contains("OCL validation pass failed"));
        Assert.Contains(findings, f => f.RuleId == Vdi3682OclRuleSet.RuleIdPrefix + "UniqueIdentifiers");
    }

    private static IEnumerable<InternalElementType> AllIes(CAEXDocument doc)
    {
        var stack = new Stack<InternalElementType>(doc.CAEXFile.InstanceHierarchy.SelectMany(ih => ih.InternalElement));
        while (stack.Count > 0)
        {
            var ie = stack.Pop();
            yield return ie;
            foreach (var child in ie.InternalElement) stack.Push(child);
        }
    }
}
