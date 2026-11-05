using System.Text.Json;
using FpbMapper.Conversion;

namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>
/// The links of the AML file the page's delete question names (016f E4, T22):
/// <see cref="ForeignLinks"/> finds them per InternalElement, the index of the
/// selection (E3, T17) places each on its layer and element of the diagram.
/// Objects the diagram does not show are left out.
/// </summary>
public static class AmlLinkList
{
    public static IReadOnlyList<AmlLinkEntry> For(IReadOnlyList<ElementLinks> found, FpdElementIndex index)
    {
        var list = new List<AmlLinkEntry>();
        foreach (var element in found)
        {
            if (!index.TryGetFpb(element.ElementId, out var fpbId, out var processId)) continue;
            foreach (var link in element.Links)
                list.Add(new AmlLinkEntry(processId, fpbId, element.ElementName, link.Link, link.Owner));
        }
        return Sorted(list);
    }

    /// <summary>A stable text of the list, to send it only when it changed.</summary>
    public static string Key(IReadOnlyList<AmlLinkEntry> links) => JsonSerializer.Serialize(Sorted(links));

    private static List<AmlLinkEntry> Sorted(IEnumerable<AmlLinkEntry> links) => links
        .OrderBy(l => l.ProcessId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(l => l.ElementId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(l => l.Link, StringComparer.OrdinalIgnoreCase)
        .ThenBy(l => l.Owner, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
