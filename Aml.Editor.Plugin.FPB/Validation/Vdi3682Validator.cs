// VDI 3682 Blatt 2 structural validator. The actual rules live in the
// Vdi3682Rules/ subfolder as IValidationRule implementations — this class only
// pre-computes a ValidationContext and hands it to every registered rule.
//
// To add a new rule: drop a new IValidationRule class into Vdi3682Rules/, list
// it in DefaultRules below. No edit to this file is needed beyond the
// registration.
//
// Reference: FPB.JS_Docs/Compliance/Schema-Abgleich-VDI3682-Blatt2.md.

using Aml.Editor.Plugin.FPB.Validation.Vdi3682Rules;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using static FpbMapper.Conversion.FpbMappings;
using static FpbMapper.Conversion.FpbTypes;

namespace Aml.Editor.Plugin.FPB.Validation;

public static class Vdi3682Validator
{
    /// <summary>
    /// The rule set applied on a normal Validate() call. Order is irrelevant
    /// because each rule appends its own findings to the list; we keep
    /// cardinality checks first because they tend to subsume related failures.
    /// </summary>
    public static readonly IReadOnlyList<IValidationRule> DefaultRules = new IValidationRule[]
    {
        new SystemLimitCardinalityRule(),
        new StateCardinalityRule(),
        new ProcessOperatorCardinalityRule(),
        new ProcessOperatorIdentificationRule(),
        new RefObjIntegrityRule(),
        new InternalLinkInterfaceRule(),
        new FlowEndpointTypingRule(),
    };

    /// <summary>Structured pass over the document. Use this for CI / programmatic consumers.</summary>
    public static List<ValidationFinding> ValidateStructured(CAEXDocument doc, ValidationOptions? options = null)
    {
        options ??= ValidationOptions.Default;
        var findings = new List<ValidationFinding>();
        if (doc?.CAEXFile == null) return findings;

        var context = new ValidationContext(
            poIds: CollectIeIds(doc, ElementToSuc[ProcessOperator]),
            processSucPath: ElementToSuc[Process]);

        foreach (var rule in DefaultRules)
        {
            if (options.IsRuleDisabled(rule.Id)) continue;
            try
            {
                rule.Validate(doc, context, findings);
            }
            catch (Exception ex)
            {
                // A single throwing rule must not derail the rest of the pass.
                findings.Add(new ValidationFinding(rule.Id, ValidationSeverity.Error,
                    $"[{rule.Id}] rule threw: {ex.Message}"));
            }
        }

        // OCL pass: the published VDI 3682 Blatt 3 constraints, executed by the
        // OclNet engine. Adds the catalogue rules the hard-coded set never covered
        // (uniqueness, naming, orphans, self-references, …); overlapping rules are
        // deduplicated inside the rule set. The hard-coded rules above remain the
        // fallback when this is switched off.
        if (options.UseOclEngine)
            Vdi3682OclRuleSet.Append(doc, options, findings);

        // Filter by minimum severity threshold (e.g. show only Errors).
        if (options.MinimumSeverity > ValidationSeverity.Info)
            findings = findings.Where(f => f.Severity >= options.MinimumSeverity).ToList();

        return findings;
    }

    /// <summary>
    /// Back-compat shim — returns the legacy List&lt;string&gt; that older callers
    /// (PluginLog.Warn) expect. New code should prefer <see cref="ValidateStructured"/>.
    /// </summary>
    public static List<string> Validate(CAEXDocument doc, ValidationOptions? options = null) =>
        ValidateStructured(doc, options).Select(f => f.ToString()).ToList();

    private static HashSet<string> CollectIeIds(CAEXDocument doc, string sucPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CaexElementWalker.WalkInternalElements(doc, ie =>
        {
            if (ie.RefBaseSystemUnitPath == sucPath && !string.IsNullOrEmpty(ie.ID))
                set.Add(ie.ID.Trim('{', '}')); // brace-free: refObj stores the uniqueIdent form
        });
        return set;
    }
}

/// <summary>
/// Caller-controlled validator options: which rules to skip, minimum severity
/// to surface.
/// </summary>
public sealed class ValidationOptions
{
    public static readonly ValidationOptions Default = new();

    /// <summary>
    /// Rule IDs that should not be executed. Useful for editor-side toggles
    /// ("don't pester me about PO Identification while I'm modelling").
    /// </summary>
    public HashSet<string> DisabledRuleIds { get; init; } = new();

    /// <summary>Lowest severity to include in the result list. Default = Info (all).</summary>
    public ValidationSeverity MinimumSeverity { get; init; } = ValidationSeverity.Info;

    /// <summary>
    /// Run the OCL engine pass (the published VDI 3682 Blatt 3 constraints via
    /// OclNet) in addition to the hard-coded rules. Default on; switch off to fall
    /// back to the hard-coded rules only.
    /// </summary>
    public bool UseOclEngine { get; init; } = true;

    public bool IsRuleDisabled(string ruleId) => DisabledRuleIds.Contains(ruleId);
}
