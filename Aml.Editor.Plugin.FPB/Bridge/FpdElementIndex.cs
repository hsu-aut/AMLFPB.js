using Aml.Engine.CAEX;
using FpbMapper.Conversion;

namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>
/// Which object of an FPD InstanceHierarchy is which element on which layer of
/// the diagram (016f E3, V26), for the selection between the AML tree and the
/// diagram and for the findings list. The rules follow
/// <see cref="CaexToFpbJson.Convert(CAEXDocument, InstanceHierarchyType)"/>:
/// <list type="bullet">
/// <item>the processes lie flat in the InstanceHierarchy; a sub-process carries
/// the id of its operator (refObj of the process, else the operator whose
/// refProcess names it), the entry process its own;</item>
/// <item>the elements of a process are its direct InternalElements, the flows
/// its InternalLinks;</item>
/// <item>the copy of a boundary state on a lower layer is a separate object
/// whose refObj names the shared id of the state.</item>
/// </list>
/// Ids are kept without braces; ids with braces are accepted.
/// </summary>
public sealed class FpdElementIndex
{
    private readonly Dictionary<string, (string FpbId, string ProcessId)> _byObject = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string FpbId, string ProcessId), string> _byElement = new(new PairComparer());
    private readonly Dictionary<string, string> _processNames = new(StringComparer.OrdinalIgnoreCase);

    public static readonly FpdElementIndex Empty = new();

    private FpdElementIndex() { }

    public static FpdElementIndex Build(InstanceHierarchyType ih)
    {
        var index = new FpdElementIndex();
        var processSuc = FpbMappings.ElementToSuc[FpbTypes.Process];
        var operatorSuc = FpbMappings.ElementToSuc[FpbTypes.ProcessOperator];
        var processes = ih.InternalElement.Where(ie => FpbMappings.StripAlias(ie.RefBaseSystemUnitPath) == processSuc).ToList();

        // operator → the sub-process it names (refProcess), for sub-processes without refObj
        var operatorOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes)
            foreach (var ie in process.InternalElement)
                if (FpbMappings.StripAlias(ie.RefBaseSystemUnitPath) == operatorSuc
                    && Bare(ie.Attribute["refProcess"]?.Value) is { Length: > 0 } named)
                    operatorOf[named] = Bare(ie.ID);

        foreach (var process in processes)
        {
            var processBare = Bare(process.ID);
            var processId = Bare(process.GetRefObjOrDerived()) is { Length: > 0 } parentOperator
                ? parentOperator
                : operatorOf.TryGetValue(processBare, out var op) ? op : processBare;
            index.Add(processBare, processId, processId);
            index._processNames[processId] = process.Name ?? "";

            foreach (var ie in process.InternalElement)
            {
                if (!FpbMappings.SucToElement.TryGetValue(FpbMappings.StripAlias(ie.RefBaseSystemUnitPath), out var fpbType)) continue;
                if (fpbType == FpbTypes.Process) continue;
                var fpbId = Bare(ie.ID);
                if (ElementMetadataRegistry.Get(fpbType)?.IsState ?? false)
                {
                    var shared = Bare(ie.GetRefObjOrDerived());
                    if (shared.Length > 0) fpbId = shared;
                }
                index.Add(Bare(ie.ID), fpbId, processId);
            }

            foreach (var link in process.InternalLink)
            {
                var linkId = Bare(link.ID);
                if (linkId.Length > 0) index.Add(linkId, linkId, processId);
            }
        }
        return index;
    }

    private void Add(string objectId, string fpbId, string processId)
    {
        if (objectId.Length == 0) return;
        _byObject[objectId] = (fpbId, processId);
        _byElement.TryAdd((fpbId, processId), objectId);
    }

    /// <summary>The element and layer of an object of the InstanceHierarchy.</summary>
    public bool TryGetFpb(string objectId, out string fpbId, out string processId)
    {
        if (_byObject.TryGetValue(Bare(objectId), out var hit))
        {
            (fpbId, processId) = hit;
            return true;
        }
        fpbId = processId = "";
        return false;
    }

    /// <summary>The id (without braces) of the object that is the element on the layer, or null.</summary>
    public string? IeIdFor(string fpbId, string processId) =>
        _byElement.TryGetValue((Bare(fpbId), Bare(processId)), out var objectId) ? objectId : null;

    /// <summary>The name of a layer: the name of its process in the AML.</summary>
    public string? ProcessName(string processId) =>
        _processNames.TryGetValue(Bare(processId), out var name) ? name : null;

    public bool Contains(string objectId) => _byObject.ContainsKey(Bare(objectId));

    private static string Bare(string? id) => (id ?? "").Trim().TrimStart('{').TrimEnd('}');

    private sealed class PairComparer : IEqualityComparer<(string FpbId, string ProcessId)>
    {
        public bool Equals((string FpbId, string ProcessId) a, (string FpbId, string ProcessId) b) =>
            StringComparer.OrdinalIgnoreCase.Equals(a.FpbId, b.FpbId) && StringComparer.OrdinalIgnoreCase.Equals(a.ProcessId, b.ProcessId);

        public int GetHashCode((string FpbId, string ProcessId) pair) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(pair.FpbId), StringComparer.OrdinalIgnoreCase.GetHashCode(pair.ProcessId));
    }
}
