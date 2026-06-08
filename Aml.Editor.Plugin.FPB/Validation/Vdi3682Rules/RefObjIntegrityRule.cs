using Aml.Engine.CAEX;
using FpbMapper.Conversion;

namespace Aml.Editor.Plugin.FPB.Validation.Vdi3682Rules;

/// <summary>
/// Decomposition integrity — a sub-process points at a ProcessOperator via
/// refObj (or any of its ETFA 2026 derivatives: refBaseObj / refExtendedObj /
/// refComposedObj). The reference must resolve to an existing PO IE; dangling
/// refs indicate that the parent PO was deleted without cleaning up the
/// sub-process. Error severity — the rendered diagram becomes inconsistent.
/// </summary>
public sealed class RefObjIntegrityRule : IValidationRule
{
    public string Id => "VDI3682.RefObjIntegrity";

    public void Validate(CAEXDocument doc, ValidationContext ctx, List<ValidationFinding> findings)
    {
        foreach (var proc in ProcessRuleHelpers.EnumerateProcesses(doc, ctx))
        {
            var refValue = proc.GetRefObjOrDerived();
            if (string.IsNullOrEmpty(refValue)) continue;            // top-level process
            if (ctx.ProcessOperatorIds.Contains(refValue)) continue;  // resolves

            findings.Add(new ValidationFinding(Id, ValidationSeverity.Error,
                $"Process '{ProcessRuleHelpers.Label(proc)}': decomposition reference '{refValue}' does not resolve to any ProcessOperator (dangling decomposition).",
                proc.ID));
        }
    }
}
