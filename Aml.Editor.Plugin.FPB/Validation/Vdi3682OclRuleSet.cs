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
        ["ProcessNamed"] = Ocl.ValidationSeverity.Warning,                         // D6
        // D7: Warning, nicht Error — die OCL-Regel hat jetzt einen Namens-Fallback,
        // aber ein Element ganz ohne Namen soll die Demo nicht rot fluten.
        ["LongNameMandatory"] = Ocl.ValidationSeverity.Warning,                    // D7
        // D8: Info — FPB.js pflegt version/revision nicht; als Warning erzeugte das
        // eine Dauer-Findings-Flut auf jedem einzelnen Element.
        ["VersionRevisionPresent"] = Ocl.ValidationSeverity.Info,                  // D8 (Blatt 2 Bild 4: Kardinalität 1)
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
    /// Last error raised by <see cref="EnsureCompiled"/>, or <c>null</c> when the
    /// most recent compile attempt succeeded.
    /// </summary>
    public static Exception? CompileError { get; private set; }

    /// <summary>
    /// Eagerly parse + compile the rule set so the first live validation doesn't
    /// pay the parse/compile latency. Returns <c>true</c> if the engine is ready.
    /// Safe to call multiple times; a transient failure clears the cache (see
    /// PublicationOnly above), so the next call retries from scratch.
    /// </summary>
    public static bool EnsureCompiled()
    {
        try
        {
            _ = Compiled.Value;
            CompileError = null;
            return true;
        }
        catch (Exception ex)
        {
            CompileError = ex;
            return false;
        }
    }

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
            var names = NamesById(doc);

            foreach (var f in validator.Validate(model, rules))
            {
                if (options.IsRuleDisabled(f.RuleId)) continue;
                var label = f.TargetId is { } id && names.TryGetValue(id.Trim().TrimStart('{').TrimEnd('}'), out var name) ? name : (f.TargetId ?? "");
                findings.Add(Finding(f.RuleId, Convert(f.Severity), label, f.TargetId));
            }

            // Silent-pass guard: an element inside an FPD process that the type
            // registry cannot classify is invisible to every rule — surface it.
            if (!options.IsRuleDisabled(UnclassifiedRuleId))
            {
                foreach (var ie in model.UnclassifiedElements().Where(IsInsideFpdProcess))
                    findings.Add(ValidationFinding.Of(UnclassifiedRuleId, ValidationSeverity.Warning,
                        "Element '{0}' has no recognised FPD type (SUC path or role) — it is invisible to all FPD rules.", new object?[] { ie.Name ?? ie.ID },
                        ie.ID));
            }
        }
        catch (Exception ex)
        {
            findings.Add(ValidationFinding.Of(RuleIdPrefix.TrimEnd('.'), ValidationSeverity.Warning,
                "OCL validation pass failed: {0}", new object?[] { ex.Message }));
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

    /// <summary>
    /// A finding of an OCL invariant as a sentence in the language of the
    /// plugin (031 V7), not the engine's "violates X". The templates are
    /// literal so the completeness test of the German texts finds them.
    /// </summary>
    public static ValidationFinding Finding(string ruleId, ValidationSeverity severity, string label, string? target)
    {
        var args = new object?[] { label };
        return (ruleId.StartsWith(RuleIdPrefix, StringComparison.Ordinal) ? ruleId[RuleIdPrefix.Length..] : ruleId) switch
        {
            "ProjectMinimumProcess" => ValidationFinding.Of(ruleId, severity, "The project '{0}' has no process.", args, target),
            "SystemLimitCardinality" => ValidationFinding.Of(ruleId, severity, "Process '{0}' does not have exactly one system limit.", args, target),
            "StateMinimumCardinality" => ValidationFinding.Of(ruleId, severity, "Process '{0}' has fewer than two states.", args, target),
            "ProcessOperatorMinimumCardinality" => ValidationFinding.Of(ruleId, severity, "Process '{0}' has no process operator.", args, target),
            "FlowEndpointsTyped" => ValidationFinding.Of(ruleId, severity, "Flow '{0}' does not connect a state with a process operator.", args, target),
            "NoStateToStateFlow" => ValidationFinding.Of(ruleId, severity, "Flow '{0}' connects two states.", args, target),
            "NoProcessOperatorToProcessOperatorFlow" => ValidationFinding.Of(ruleId, severity, "Flow '{0}' connects two process operators.", args, target),
            "UsageEndpointsTyped" => ValidationFinding.Of(ruleId, severity, "Usage '{0}' does not connect a process operator with a technical resource.", args, target),
            "NoDuplicateConnections" => ValidationFinding.Of(ruleId, severity, "Process '{0}' has duplicate connections.", args, target),
            "FlowDirected" => ValidationFinding.Of(ruleId, severity, "Flow '{0}' does not run from an output to an input.", args, target),
            "NoMixedFlowTypes" => ValidationFinding.Of(ruleId, severity, "Process operator '{0}' mixes a normal flow with parallel or alternative flows.", args, target),
            "UniqueIdentifiers" => ValidationFinding.Of(ruleId, severity, "Several elements of '{0}' share a unique ID.", args, target),
            "ProcessNamed" => ValidationFinding.Of(ruleId, severity, "Process '{0}' has no name.", args, target),
            "LongNameMandatory" => ValidationFinding.Of(ruleId, severity, "Element '{0}' has no name.", args, target),
            "VersionRevisionPresent" => ValidationFinding.Of(ruleId, severity, "Element '{0}' has no version or revision number.", args, target),
            "RefObjResolvable" => ValidationFinding.Of(ruleId, severity, "Element '{0}' refers with refObj to an element that does not exist.", args, target),
            "RefProcessResolvable" => ValidationFinding.Of(ruleId, severity, "Process operator '{0}' refers to a decomposition that does not exist.", args, target),
            "AllReferencesResolvable" => ValidationFinding.Of(ruleId, severity, "Element '{0}' has a reference that does not resolve.", args, target),
            "ProcessOperatorHasIO" => ValidationFinding.Of(ruleId, severity, "Process operator '{0}' lacks an input or an output.", args, target),
            "StateHasConcreteType" => ValidationFinding.Of(ruleId, severity, "State '{0}' is neither a product, an energy nor an information.", args, target),
            "NoSelfReference" => ValidationFinding.Of(ruleId, severity, "Connection '{0}' connects an element with itself.", args, target),
            "NoOrphanedElements" => ValidationFinding.Of(ruleId, severity, "Element '{0}' is not connected.", args, target),
            "SourceTargetSameProcess" => ValidationFinding.Of(ruleId, severity, "Connection '{0}' joins elements of two processes.", args, target),
            var other => ValidationFinding.Of(ruleId, severity, "Element '{0}' breaks rule {1}.", new object?[] { label, other }, target),
        };
    }

    /// <summary>Names of the elements and links of the document by their bare ID, for the sentences above.</summary>
    private static Dictionary<string, string> NamesById(CAEXDocument doc)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Walk(IEnumerable<InternalElementType> elements)
        {
            foreach (var ie in elements)
            {
                if (!string.IsNullOrEmpty(ie.ID) && !string.IsNullOrEmpty(ie.Name))
                    names.TryAdd(ie.ID.Trim().TrimStart('{').TrimEnd('}'), ie.Name);
                foreach (var link in ie.InternalLink)
                    if (!string.IsNullOrEmpty(link.ID) && !string.IsNullOrEmpty(link.Name))
                        names.TryAdd(link.ID.Trim().TrimStart('{').TrimEnd('}'), link.Name);
                Walk(ie.InternalElement);
            }
        }
        foreach (var ih in doc.CAEXFile?.InstanceHierarchy ?? Enumerable.Empty<InstanceHierarchyType>())
            Walk(ih.InternalElement);
        return names;
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
