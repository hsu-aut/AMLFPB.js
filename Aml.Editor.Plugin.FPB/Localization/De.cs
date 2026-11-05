// German texts of the plugin, keyed by their English (016h F2). AutomationML
// terms stay as the standard names them (InstanceHierarchy, InternalLink,
// SystemUnitClass), as German users know them; terms of VDI 3682 as the
// guideline names them; "du" and the imperative as in the OPC UA plugin.

namespace Aml.Editor.Plugin.FPB.Localization;

public static class De
{
    public static readonly IReadOnlyDictionary<string, string> Texts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Updating '{0}' …"] = "'{0}' wird übernommen …",
    };
}
