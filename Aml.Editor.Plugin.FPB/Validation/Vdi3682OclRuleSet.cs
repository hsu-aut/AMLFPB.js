using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using OCL.NET.Caex;
using OCL.NET.Core.Parser;
using Ocl = OCL.NET.Core.Validation;

namespace Aml.Editor.Plugin.FPB.Validation;

/// <summary>
/// The OCL validation pass: runs the FPD OCL rule set
/// (<see cref="FpbValidationRules.PureRules"/> + <see cref="FpbValidationRules.Helpers"/>,
/// shipped from FpbMapper.Conversion so plugin and web backend share the same
/// source) through the OCL.NET engine and appends the findings to the structural
/// validator's list.
///
/// Rules whose ground the hard-coded <see cref="IValidationRule"/>s already cover
/// are skipped by default to avoid duplicate findings; the hard-coded rules stay
/// authoritative (and act as the fallback when the OCL pass is disabled via
/// <see cref="ValidationOptions.UseOclEngine"/>). The OCL pass therefore *adds*
/// the rules that were never hard-coded (uniqueness, naming, orphan detection,
/// self-references, …).
/// </summary>
public static class Vdi3682OclRuleSet
{
    public const string RuleIdPrefix = "VDI3682.OCL.";
    public const string UnclassifiedRuleId = RuleIdPrefix + "UnclassifiedElement";

    /// <summary>Severity per rule, keyed by invariant name. See the FPD validation rule set.</summary>
    private static readonly Dictionary<string, Ocl.ValidationSeverity> CatalogueSeverity = new(StringComparer.Ordinal)
    {
        ["ProjectMinimumProcess"] = Ocl.ValidationSeverity.Error,                  // A1
        ["SystemLimitCardinality"] = Ocl.ValidationSeverity.Error,                 // A2
        ["StateMinimumCardinality"] = Ocl.ValidationSeverity.Warning,              // A3
        ["ProcessOperatorMinimumCardinality"] = Ocl.ValidationSeverity.Warning,    // A4
        ["FlowEndpointsTyped"] = Ocl.ValidationSeverity.Error,                     // C1
        ["NoStateToStateFlow"] = Ocl.ValidationSeverity.Error,                     // C2
        ["NoProcessOperatorToProcessOperatorFlow"] = Ocl.ValidationSeverity.Error, // C3
        ["UsageEndpointsTyped"] = Ocl.ValidationSeverity.Error,                    // C4
        ["NoDuplicateConnections"] = Ocl.ValidationSeverity.Warning,               // C5
        ["FlowDirected"] = Ocl.ValidationSeverity.Error,                           // C6
        ["NoMixedFlowTypes"] = Ocl.ValidationSeverity.Warning,                     // C9
        ["UniqueIdentifiers"] = Ocl.ValidationSeverity.Error,                      // D1
        ["ProcessOperatorNamed"] = Ocl.ValidationSeverity.Info,                    // D3
        ["StateNamed"] = Ocl.ValidationSeverity.Info,                              // D4
        ["TechnicalResourceNamed"] = Ocl.ValidationSeverity.Info,                  // D5
        ["ProcessNamed"] = Ocl.ValidationSeverity.Warning,                         // D6
        ["LongNameMandatory"] = Ocl.ValidationSeverity.Error,                      // D7
        ["VersionRevisionPresent"] = Ocl.ValidationSeverity.Warning,               // D8 (Blatt 2 Bild 4: Kardinalität 1)
        ["RefObjResolvable"] = Ocl.ValidationSeverity.Error,                       // F1
        ["RefProcessResolvable"] = Ocl.ValidationSeverity.Error,                   // F2
        ["AllReferencesResolvable"] = Ocl.ValidationSeverity.Error,                // F3
        ["ProcessOperatorHasIO"] = Ocl.ValidationSeverity.Warning,                 // G1
        ["StateHasConcreteType"] = Ocl.ValidationSeverity.Error,                   // G2
        ["NoSelfReference"] = Ocl.ValidationSeverity.Error,                        // G3
        ["NoOrphanedElements"] = Ocl.ValidationSeverity.Warning,                   // G4
        ["SourceTargetSameProcess"] = Ocl.ValidationSeverity.Error,                // I3
    };

    /// <summary>Invariants whose ground the hard-coded rules already check (skipped at runtime to avoid double findings).</summary>
    private static readonly HashSet<string> CoveredByHardcoded = new(StringComparer.Ordinal)
    {
        "SystemLimitCardinality",              // VDI3682.SystemLimitCardinality
        "StateMinimumCardinality",             // VDI3682.StateCardinality
        "ProcessOperatorMinimumCardinality",   // VDI3682.ProcessOperatorCardinality
        "ProcessOperatorNamed",                // VDI3682.ProcessOperatorIdentification
        "FlowEndpointsTyped",                  // VDI3682.FlowEndpointTyping
        "UsageEndpointsTyped",                 // VDI3682.FlowEndpointTyping
        "FlowDirected",                        // VDI3682.InternalLinkInterface (FlowOut/FlowIn-Paarung über kanonische Pfade)
    };

    // PublicationOnly: a transient failure in the factory (e.g. resource IO) is NOT
    // cached — the default mode would freeze the first exception for the whole
    // editor session and kill the OCL pass permanently.
    private static readonly Lazy<(Ocl.OclValidator Validator, Ocl.CompiledRuleSet Runtime)> Compiled = new(() =>
    {
        var validator = new Ocl.OclValidator();
        var specs = LoadRuleSpecs(includeHardcodedCovered: false);
        var definitions = new OclParser().ParseDefinitions(FpbValidationRules.Helpers);
        return (validator, validator.Compile(specs, definitions));
    }, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>
    /// Build the rule specs from the embedded artifact. Exposed so tests can run the
    /// exact runtime rule source (incl. the hard-coded-covered rules for parity checks).
    /// </summary>
    public static List<Ocl.OclRuleSpec> LoadRuleSpecs(bool includeHardcodedCovered)
    {
        var parser = new OclParser();
        var specs = new List<Ocl.OclRuleSpec>();
        foreach (var block in SplitRules(FpbValidationRules.PureRules))
        {
            // Per-block isolation: a single malformed rule must not take down the
            // whole rule set — the name is recovered via regex and the block is kept,
            // so OclValidator.Compile reports its parse error as a per-rule finding.
            string name;
            try
            {
                name = parser.ParseConstraint(block).Name ?? "?";
            }
            catch (OclParseException)
            {
                name = System.Text.RegularExpressions.Regex.Match(block, @"inv\s+(\w+)").Groups[1].Value;
                if (string.IsNullOrEmpty(name)) name = "ParseError";
            }
            if (!includeHardcodedCovered && CoveredByHardcoded.Contains(name)) continue;
            specs.Add(new Ocl.OclRuleSpec(
                RuleIdPrefix + name,
                CatalogueSeverity.GetValueOrDefault(name, Ocl.ValidationSeverity.Warning),
                "FPD validation rule set",
                block));
        }
        return specs;
    }

    /// <summary>The helper <c>def:</c> operations from the embedded artifact (for tests running the engine directly).</summary>
    public static IReadOnlyList<OCL.NET.Core.Ast.OclOperationDef> LoadDefinitions() =>
        new OclParser().ParseDefinitions(FpbValidationRules.Helpers);

    /// <summary>
    /// Run the OCL pass over <paramref name="doc"/> and append plugin findings.
    /// Never throws — an engine failure becomes a single diagnostic finding so the
    /// structural validation result is preserved.
    /// </summary>
    public static void Append(CAEXDocument doc, ValidationOptions options, List<ValidationFinding> findings)
    {
        try
        {
            var model = new CaexMetamodel(doc);

            // A document without any FPD process is none of our business — without
            // this guard, ProjectMinimumProcess would flag every foreign AML file
            // opened in the editor as an Error.
            if (!model.InstancesOf("FPD_Process").Any()) return;

            var (validator, rules) = Compiled.Value;

            foreach (var f in validator.Validate(model, rules))
            {
                if (options.IsRuleDisabled(f.RuleId)) continue;
                findings.Add(new ValidationFinding(f.RuleId, Convert(f.Severity), f.Message, f.TargetId));
            }

            // Silent-pass guard: an element inside an FPD process that the type
            // registry cannot classify is invisible to every rule — surface it.
            if (!options.IsRuleDisabled(UnclassifiedRuleId))
            {
                foreach (var ie in model.UnclassifiedElements().Where(IsInsideFpdProcess))
                    findings.Add(new ValidationFinding(UnclassifiedRuleId, ValidationSeverity.Warning,
                        $"Element '{ie.Name ?? ie.ID}' has no recognised FPD type (SUC path or role) — it is invisible to all FPD rules.",
                        ie.ID));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new ValidationFinding(RuleIdPrefix.TrimEnd('.'), ValidationSeverity.Warning,
                $"OCL validation pass failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Scope the unclassified warning to FPD content — foreign IHs in a mixed AML
    /// document are none of our business. Uses the type registry (SUC path OR role
    /// requirements), so role-typed FPD processes are recognised too.
    /// </summary>
    private static bool IsInsideFpdProcess(InternalElementType element)
    {
        var registry = new FpdTypeRegistry();
        var current = element.CAEXParent as InternalElementType;
        while (current is not null)
        {
            if (registry.IsKindOf(current, registry.ProcessTypeName)) return true;
            current = current.CAEXParent as InternalElementType;
        }
        return false;
    }

    private static ValidationSeverity Convert(Ocl.ValidationSeverity severity) => severity switch
    {
        Ocl.ValidationSeverity.Error => ValidationSeverity.Error,
        Ocl.ValidationSeverity.Warning => ValidationSeverity.Warning,
        _ => ValidationSeverity.Info,
    };

    private static IEnumerable<string> SplitRules(string spec) =>
        spec.Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim())
            .Where(b => b.StartsWith("context", StringComparison.Ordinal));

}
