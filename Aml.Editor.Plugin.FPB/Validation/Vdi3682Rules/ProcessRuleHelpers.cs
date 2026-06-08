using Aml.Engine.CAEX;

namespace Aml.Editor.Plugin.FPB.Validation.Vdi3682Rules;

/// <summary>
/// Shared helpers used by multiple rule implementations. Kept internal so the
/// rule API surface stays minimal.
/// </summary>
internal static class ProcessRuleHelpers
{
    public static IEnumerable<InternalElementType> EnumerateProcesses(CAEXDocument doc, ValidationContext ctx)
    {
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
        {
            foreach (var proc in ih.InternalElement.Where(ie => ie.RefBaseSystemUnitPath == ctx.ProcessSucPath))
                yield return proc;
        }
    }

    public static string Label(InternalElementType proc) =>
        string.IsNullOrEmpty(proc.Name) ? (proc.ID ?? "?") : proc.Name;
}
