using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using static FpbMapper.Conversion.FpbMappings;
using static FpbMapper.Conversion.FpbTypes;

namespace Aml.Editor.Plugin.FPB.Validation.Vdi3682Rules;

/// <summary>
/// VDI 3682 Bild 2: every Process IE must contain exactly one SystemLimit IE.
/// Hard rule — a missing SystemLimit means the model cannot be rendered.
/// </summary>
public sealed class SystemLimitCardinalityRule : IValidationRule
{
    public string Id => "VDI3682.SystemLimitCardinality";

    public void Validate(CAEXDocument doc, ValidationContext ctx, List<ValidationFinding> findings)
    {
        foreach (var proc in ProcessRuleHelpers.EnumerateProcesses(doc, ctx))
        {
            var procLabel = ProcessRuleHelpers.Label(proc);
            var slCount = proc.InternalElement.Count(ie =>
                SucToElement.GetValueOrDefault(ie.RefBaseSystemUnitPath ?? "", "") == SystemLimit);

            if (slCount == 0)
                findings.Add(ValidationFinding.Of(Id, ValidationSeverity.Error,
                    "Process '{0}': missing SystemLimit (VDI 3682 Bild 2: cardinality exactly 1).", new object?[] { procLabel },
                    proc.ID));
            else if (slCount > 1)
                findings.Add(ValidationFinding.Of(Id, ValidationSeverity.Error,
                    "Process '{0}': {1} SystemLimits found, expected exactly 1.", new object?[] { procLabel, slCount },
                    proc.ID));
        }
    }
}

/// <summary>VDI 3682 Bild 2: a Process must contain at least two states. Warning — model still loads.</summary>
public sealed class StateCardinalityRule : IValidationRule
{
    public string Id => "VDI3682.StateCardinality";

    public void Validate(CAEXDocument doc, ValidationContext ctx, List<ValidationFinding> findings)
    {
        foreach (var proc in ProcessRuleHelpers.EnumerateProcesses(doc, ctx))
        {
            var stateCount = proc.InternalElement.Count(ie =>
                StateTypes.Contains(SucToElement.GetValueOrDefault(ie.RefBaseSystemUnitPath ?? "", "")));

            if (stateCount < 2)
                findings.Add(ValidationFinding.Of(Id, ValidationSeverity.Warning,
                    "Process '{0}': only {1} state(s), expected ≥2 (VDI 3682 Bild 2).", new object?[] { ProcessRuleHelpers.Label(proc), stateCount },
                    proc.ID));
        }
    }
}

/// <summary>VDI 3682 Bild 2: a Process must contain at least one ProcessOperator. Warning.</summary>
public sealed class ProcessOperatorCardinalityRule : IValidationRule
{
    public string Id => "VDI3682.ProcessOperatorCardinality";

    public void Validate(CAEXDocument doc, ValidationContext ctx, List<ValidationFinding> findings)
    {
        foreach (var proc in ProcessRuleHelpers.EnumerateProcesses(doc, ctx))
        {
            var poCount = proc.InternalElement.Count(ie =>
                SucToElement.GetValueOrDefault(ie.RefBaseSystemUnitPath ?? "", "") == ProcessOperator);

            if (poCount < 1)
                findings.Add(ValidationFinding.Of(Id, ValidationSeverity.Warning,
                    "Process '{0}': no ProcessOperator (VDI 3682 Bild 2: cardinality 1..*).", new object?[] { ProcessRuleHelpers.Label(proc) },
                    proc.ID));
        }
    }
}
