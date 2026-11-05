using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using static FpbMapper.Conversion.FpbMappings;
using static FpbMapper.Conversion.FpbTypes;

namespace Aml.Editor.Plugin.FPB.Validation.Vdi3682Rules;

/// <summary>
/// Every InternalLink's A-/B-interface must use one of the canonical FPD
/// InterfaceClass paths. Stray paths indicate a hand-crafted link that the
/// mapper cannot interpret. Warning severity — the link is silently skipped
/// at render time.
/// </summary>
public sealed class InternalLinkInterfaceRule : IValidationRule
{
    public string Id => "VDI3682.InternalLinkInterface";

    private static readonly HashSet<string> ValidOutPaths =
        new(FlowToInterface.Values.Select(v => v.Out), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidInPaths =
        new(FlowToInterface.Values.Select(v => v.In), StringComparer.OrdinalIgnoreCase);

    public void Validate(CAEXDocument doc, ValidationContext ctx, List<ValidationFinding> findings)
    {
        foreach (var proc in ProcessRuleHelpers.EnumerateProcesses(doc, ctx))
        {
            var procLabel = ProcessRuleHelpers.Label(proc);
            foreach (var link in proc.InternalLink)
            {
                var aIf = link.AInterface as ExternalInterfaceType;
                var bIf = link.BInterface as ExternalInterfaceType;
                if (aIf == null || bIf == null)
                {
                    findings.Add(ValidationFinding.Of(Id, ValidationSeverity.Warning,
                        "Process '{0}': link '{1}' has no resolvable A-/B-interface.", new object?[] { procLabel, link.Name }));
                    continue;
                }
                var aPath = aIf.RefBaseClassPath ?? "";
                var bPath = bIf.RefBaseClassPath ?? "";
                if (!ValidOutPaths.Contains(aPath))
                    findings.Add(ValidationFinding.Of(Id, ValidationSeverity.Warning,
                        "Process '{0}': link '{1}' uses unknown source interface '{2}'.", new object?[] { procLabel, link.Name, aPath }));
                if (!ValidInPaths.Contains(bPath))
                    findings.Add(ValidationFinding.Of(Id, ValidationSeverity.Warning,
                        "Process '{0}': link '{1}' uses unknown target interface '{2}'.", new object?[] { procLabel, link.Name, bPath }));
            }
        }
    }
}

/// <summary>
/// VDI 3682 flow-endpoint typing — Flow/ParallelFlow/AlternativeFlow always
/// connect a State and a ProcessOperator; Usage always connects a
/// ProcessOperator and a TechnicalResource. Warning severity — endpoint type
/// mismatches are recoverable but indicate user error.
/// </summary>
public sealed class FlowEndpointTypingRule : IValidationRule
{
    public string Id => "VDI3682.FlowEndpointTyping";

    private static readonly string UsageOutPath = FlowToInterface[Usage].Out;

    public void Validate(CAEXDocument doc, ValidationContext ctx, List<ValidationFinding> findings)
    {
        foreach (var proc in ProcessRuleHelpers.EnumerateProcesses(doc, ctx))
        {
            var procLabel = ProcessRuleHelpers.Label(proc);
            foreach (var link in proc.InternalLink)
            {
                var aIf = link.AInterface as ExternalInterfaceType;
                var bIf = link.BInterface as ExternalInterfaceType;
                if (aIf == null || bIf == null) continue;

                var sourceIe = aIf.CAEXParent as InternalElementType;
                var targetIe = bIf.CAEXParent as InternalElementType;
                if (sourceIe == null || targetIe == null) continue;

                var sourceFpb = SucToElement.GetValueOrDefault(sourceIe.RefBaseSystemUnitPath ?? "", "");
                var targetFpb = SucToElement.GetValueOrDefault(targetIe.RefBaseSystemUnitPath ?? "", "");
                var aPath = aIf.RefBaseClassPath ?? "";

                if (string.Equals(aPath, UsageOutPath, StringComparison.OrdinalIgnoreCase))
                {
                    var ok =
                           (sourceFpb == ProcessOperator   && targetFpb == TechnicalResource)
                        || (sourceFpb == TechnicalResource && targetFpb == ProcessOperator);
                    if (!ok)
                        findings.Add(ValidationFinding.Of(Id, ValidationSeverity.Warning,
                            "Process '{0}': Usage '{1}' connects {2}→{3}, expected PO↔TR.", new object?[] { procLabel, link.Name, sourceFpb, targetFpb }));
                }
                else
                {
                    var ok =
                           (StateTypes.Contains(sourceFpb) && targetFpb == ProcessOperator)
                        || (sourceFpb == ProcessOperator && StateTypes.Contains(targetFpb));
                    if (!ok)
                        findings.Add(ValidationFinding.Of(Id, ValidationSeverity.Warning,
                            "Process '{0}': Flow '{1}' connects {2}→{3}, expected State↔PO.", new object?[] { procLabel, link.Name, sourceFpb, targetFpb }));
                }
            }
        }
    }
}
