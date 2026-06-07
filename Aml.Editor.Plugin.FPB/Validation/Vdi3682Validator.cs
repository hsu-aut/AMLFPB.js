// Lightweight structural validator against VDI 3682 Blatt 2.
//
// Rules implemented (from FPB.JS_Docs/Compliance/Schema-Abgleich-VDI3682-Blatt2.md):
//   Process : SystemLimit       — cardinality 1
//   Process : States            — cardinality 2..*
//   Process : ProcessOperator   — cardinality 1..*
//   Flow types must be one of the canonical fpb: values (Flow / ParallelFlow /
//   AlternativeFlow / Usage). InternalLinks resolved via RefBaseClassPath.
//   Sub-Process refObj must resolve to an existing ProcessOperator IE.
//   Flow endpoint typing: Flow source ∈ {States}, target ∈ {PO}; Usage between PO and TR.
//   ProcessOperator should carry an Identification with a non-empty long/shortName.
//
// The validator is read-only — it never mutates the document. Output is a list
// of human-readable warning strings the plugin surfaces via PluginLog.Warn.

using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using static FpbMapper.Conversion.FpbMappings;

namespace Aml.Editor.Plugin.FPB.Validation;

public static class Vdi3682Validator
{
    public static List<string> Validate(CAEXDocument doc)
    {
        var warnings = new List<string>();
        if (doc?.CAEXFile == null) return warnings;

        // Pre-pass: index every PO IE across the document so sub-process refObj
        // integrity can be checked in O(1) per process. Stored case-insensitively
        // because CAEX/AML element IDs are technically case-sensitive but tooling
        // commonly normalises them.
        var poIds = CollectIeIds(doc, ElementToSuc["fpb:ProcessOperator"]);

        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
            ValidateInstanceHierarchy(ih, poIds, warnings);

        return warnings;
    }

    private static HashSet<string> CollectIeIds(CAEXDocument doc, string sucPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
            WalkIes(ih.InternalElement, ie =>
            {
                if (ie.RefBaseSystemUnitPath == sucPath && !string.IsNullOrEmpty(ie.ID))
                    set.Add(ie.ID);
            });
        return set;
    }

    private static void WalkIes(IEnumerable<InternalElementType> roots, Action<InternalElementType> visit)
    {
        foreach (var ie in roots)
        {
            visit(ie);
            WalkIes(ie.InternalElement, visit);
        }
    }

    private static void ValidateInstanceHierarchy(InstanceHierarchyType ih, HashSet<string> poIds, List<string> warnings)
    {
        var processSuc = ElementToSuc["fpb:Process"];
        foreach (var proc in ih.InternalElement.Where(ie => ie.RefBaseSystemUnitPath == processSuc))
            ValidateProcess(proc, poIds, warnings);
    }

    private static void ValidateProcess(InternalElementType proc, HashSet<string> poIds, List<string> warnings)
    {
        var procLabel = string.IsNullOrEmpty(proc.Name) ? proc.ID ?? "?" : proc.Name;
        var children = proc.InternalElement.ToList();

        // Helper: classify each child IE by its FPD type via SucToElement.
        int slCount = 0, poCount = 0, stateCount = 0;
        foreach (var ie in children)
        {
            if (!SucToElement.TryGetValue(ie.RefBaseSystemUnitPath, out var fpbType)) continue;
            switch (fpbType)
            {
                case "fpb:SystemLimit":     slCount++; break;
                case "fpb:ProcessOperator": poCount++; ValidateProcessOperator(ie, procLabel, warnings); break;
                default:
                    if (StateTypes.Contains(fpbType)) stateCount++;
                    break;
            }
        }

        if (slCount == 0)
            warnings.Add($"Process '{procLabel}': missing SystemLimit (VDI 3682 Bild 2: cardinality exactly 1).");
        else if (slCount > 1)
            warnings.Add($"Process '{procLabel}': {slCount} SystemLimits found, expected exactly 1.");

        if (stateCount < 2)
            warnings.Add($"Process '{procLabel}': only {stateCount} state(s), expected ≥2 (VDI 3682 Bild 2).");

        if (poCount < 1)
            warnings.Add($"Process '{procLabel}': no ProcessOperator (VDI 3682 Bild 2: cardinality 1..*).");

        // Sub-process refObj must resolve to a real ProcessOperator IE (decomposition
        // integrity). Top-level processes have empty refObj and skip the check.
        var refObj = proc.Attribute["refObj"]?.Value;
        if (!string.IsNullOrEmpty(refObj) && !poIds.Contains(refObj))
            warnings.Add($"Process '{procLabel}': refObj '{refObj}' does not resolve to any ProcessOperator (dangling decomposition).");

        // Flow types on InternalLinks — InternalLink.AInterface.RefBaseClassPath
        // must resolve to one of the canonical FPD interface paths.
        foreach (var link in proc.InternalLink)
            ValidateInternalLink(link, proc, procLabel, warnings);
    }

    /// <summary>
    /// Non-empty Identification.longName OR shortName OR ie.Name is required —
    /// per VDI 3682 Bild 4. We accept any of the three so the user isn't pestered
    /// when only the visual label is set.
    /// </summary>
    private static void ValidateProcessOperator(InternalElementType po, string procLabel, List<string> warnings)
    {
        var ident = po.Attribute["Identification"];
        var longName  = ident?.Attribute["longName"]?.Value;
        var shortName = ident?.Attribute["shortName"]?.Value;
        if (string.IsNullOrWhiteSpace(longName)
            && string.IsNullOrWhiteSpace(shortName)
            && string.IsNullOrWhiteSpace(po.Name))
        {
            var poLabel = po.ID ?? "?";
            warnings.Add($"Process '{procLabel}': ProcessOperator '{poLabel}' has no name/Identification (VDI 3682 Bild 4).");
        }
    }

    private static readonly HashSet<string> ValidFlowOutPaths =
        new(FlowToInterface.Values.Select(v => v.Out), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidFlowInPaths =
        new(FlowToInterface.Values.Select(v => v.In), StringComparer.OrdinalIgnoreCase);
    private static readonly string UsageOutPath = FlowToInterface["fpb:Usage"].Out;

    private static void ValidateInternalLink(InternalLinkType link, InternalElementType proc, string procLabel, List<string> warnings)
    {
        var aIface = link.AInterface as ExternalInterfaceType;
        var bIface = link.BInterface as ExternalInterfaceType;
        if (aIface == null || bIface == null)
        {
            warnings.Add($"Process '{procLabel}': link '{link.Name}' has no resolvable A-/B-interface.");
            return;
        }

        var aPath = aIface.RefBaseClassPath ?? "";
        var bPath = bIface.RefBaseClassPath ?? "";
        if (!ValidFlowOutPaths.Contains(aPath))
            warnings.Add($"Process '{procLabel}': link '{link.Name}' uses unknown source interface '{aPath}'.");
        if (!ValidFlowInPaths.Contains(bPath))
            warnings.Add($"Process '{procLabel}': link '{link.Name}' uses unknown target interface '{bPath}'.");

        // Flow-endpoint typing: Flow / ParallelFlow / AlternativeFlow run between a
        // State and a ProcessOperator (either direction). Usage runs between a
        // ProcessOperator and a TechnicalResource. Anything else is a model defect
        // even if both interfaces are individually canonical.
        var sourceIe = aIface.CAEXParent as InternalElementType;
        var targetIe = bIface.CAEXParent as InternalElementType;
        if (sourceIe == null || targetIe == null) return;

        var sourceFpb = SucToElement.GetValueOrDefault(sourceIe.RefBaseSystemUnitPath ?? "", "");
        var targetFpb = SucToElement.GetValueOrDefault(targetIe.RefBaseSystemUnitPath ?? "", "");

        if (string.Equals(aPath, UsageOutPath, StringComparison.OrdinalIgnoreCase))
        {
            // Usage: PO ↔ TR in either direction.
            var bothEndpointsValid =
                   (sourceFpb == "fpb:ProcessOperator" && targetFpb == "fpb:TechnicalResource")
                || (sourceFpb == "fpb:TechnicalResource" && targetFpb == "fpb:ProcessOperator");
            if (!bothEndpointsValid)
                warnings.Add($"Process '{procLabel}': Usage '{link.Name}' connects {sourceFpb}→{targetFpb}, expected PO↔TR.");
        }
        else
        {
            // Flow / ParallelFlow / AlternativeFlow: State ↔ PO.
            var sourceIsState  = StateTypes.Contains(sourceFpb);
            var targetIsState  = StateTypes.Contains(targetFpb);
            var sourceIsPo     = sourceFpb == "fpb:ProcessOperator";
            var targetIsPo     = targetFpb == "fpb:ProcessOperator";
            var bothEndpointsValid =
                   (sourceIsState && targetIsPo)
                || (sourceIsPo && targetIsState);
            if (!bothEndpointsValid)
                warnings.Add($"Process '{procLabel}': Flow '{link.Name}' connects {sourceFpb}→{targetFpb}, expected State↔PO.");
        }
    }
}
