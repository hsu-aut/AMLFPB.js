using Aml.Engine.CAEX;

namespace Aml.Editor.Plugin.FPB.Validation;

/// <summary>
/// Triage for a finding. Plugin surfaces all of them, but consumers (CI, UI)
/// can filter by severity — e.g. block a save only on <see cref="Error"/>.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>Informational, no model defect (e.g. "stamped library version 1.0.0").</summary>
    Info,
    /// <summary>Soft rule violation, model still loads (e.g. PO without Identification).</summary>
    Warning,
    /// <summary>Hard rule violation, model is structurally broken (e.g. no SystemLimit).</summary>
    Error,
}

/// <summary>
/// A single validation finding with structured fields. Replaces the loose
/// warning-string list once rules opt in. <see cref="ToString"/> renders a
/// human-readable line for the legacy string-list path.
/// </summary>
public sealed class ValidationFinding
{
    public ValidationFinding(string ruleId, ValidationSeverity severity, string message, string? targetIeId = null)
    {
        RuleId = ruleId;
        Severity = severity;
        Message = message;
        TargetIeId = targetIeId;
    }

    public string RuleId { get; }
    public ValidationSeverity Severity { get; }
    public string Message { get; }

    /// <summary>Optional: AML ID of the IE the finding points at, for editor focus jumps.</summary>
    public string? TargetIeId { get; }

    public override string ToString() => $"[{Severity}] {Message}";
}

/// <summary>
/// One discrete FPD structural rule. Each rule receives the document plus a
/// pre-computed <see cref="ValidationContext"/> and appends findings to it.
/// Rules MUST NOT mutate the document.
///
/// Adding a new rule is a matter of dropping a new class into
/// <c>Vdi3682Rules/</c> and registering it in
/// <see cref="Vdi3682Validator.DefaultRules"/>.
/// </summary>
public interface IValidationRule
{
    /// <summary>Short identifier used in log messages, e.g. <c>"VDI3682.SystemLimitCardinality"</c>.</summary>
    string Id { get; }

    void Validate(CAEXDocument doc, ValidationContext context, List<ValidationFinding> findings);
}

/// <summary>
/// Pre-computed cross-document lookups passed to every rule. Computed once per
/// validate-pass to keep individual rules O(1) on shared data.
/// </summary>
public sealed class ValidationContext
{
    public ValidationContext(HashSet<string> poIds, string processSucPath)
    {
        ProcessOperatorIds = poIds;
        ProcessSucPath = processSucPath;
    }

    /// <summary>IDs of every ProcessOperator IE in the document (any IH).</summary>
    public HashSet<string> ProcessOperatorIds { get; }

    /// <summary>The SUC path identifying FPD_Process IEs.</summary>
    public string ProcessSucPath { get; }
}
