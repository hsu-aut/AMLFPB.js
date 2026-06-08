using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using static FpbMapper.Conversion.FpbMappings;
using static FpbMapper.Conversion.FpbTypes;

namespace Aml.Editor.Plugin.FPB.Validation.Vdi3682Rules;

/// <summary>
/// VDI 3682 Bild 4: every ProcessOperator should carry a name —
/// Identification.longName, Identification.shortName, or at minimum the
/// IE.Name. Info severity — model is structurally fine, just under-documented.
/// </summary>
public sealed class ProcessOperatorIdentificationRule : IValidationRule
{
    public string Id => "VDI3682.ProcessOperatorIdentification";

    public void Validate(CAEXDocument doc, ValidationContext ctx, List<ValidationFinding> findings)
    {
        foreach (var proc in ProcessRuleHelpers.EnumerateProcesses(doc, ctx))
        {
            var procLabel = ProcessRuleHelpers.Label(proc);
            foreach (var po in proc.InternalElement.Where(ie =>
                SucToElement.GetValueOrDefault(ie.RefBaseSystemUnitPath ?? "", "") == ProcessOperator))
            {
                var ident = po.Attribute[IdentificationSchema.AttributeName];
                var longName  = ident?.Attribute[IdentificationSchema.LongName]?.Value;
                var shortName = ident?.Attribute[IdentificationSchema.ShortName]?.Value;
                if (string.IsNullOrWhiteSpace(longName)
                    && string.IsNullOrWhiteSpace(shortName)
                    && string.IsNullOrWhiteSpace(po.Name))
                {
                    var poLabel = po.ID ?? "?";
                    findings.Add(new ValidationFinding(Id, ValidationSeverity.Info,
                        $"Process '{procLabel}': ProcessOperator '{poLabel}' has no name/Identification (VDI 3682 Bild 4).",
                        po.ID));
                }
            }
        }
    }
}
