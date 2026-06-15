using System.Windows.Media;
using Aml.Editor.Plugin.FPB.Validation;
using Aml.Engine.CAEX;

namespace Aml.Editor.Plugin.FPB.Views;

/// <summary>
/// Display-side projection of a <see cref="ValidationFinding"/> for the
/// findings DataGrid in IhView. Adds presentation helpers (glyph, brush,
/// sortable rank, resolved element label) without dragging UI concerns into
/// the validation layer itself.
/// </summary>
public sealed class FindingRow
{
    private FindingRow() { }

    public string RuleId { get; private init; } = "";
    public string Message { get; private init; } = "";
    public string ElementId { get; private init; } = "";
    public string ElementLabel { get; private init; } = "";

    public string SeverityName { get; private init; } = "";
    public string SeverityGlyph { get; private init; } = "";
    public Brush SeverityBrush { get; private init; } = Brushes.Gray;
    public int SeverityRank { get; private init; }

    /// <summary>
    /// Build a row from a finding. <paramref name="doc"/> is used to look up a
    /// friendlier element label (IE name) — when it is null or the lookup
    /// misses we fall back to the bare id.
    /// </summary>
    public static FindingRow From(ValidationFinding f, CAEXDocument? doc)
    {
        var id = f.TargetIeId ?? "";
        var label = string.IsNullOrEmpty(id) ? "" : (LookupName(doc, id) ?? id);

        return new FindingRow
        {
            RuleId = f.RuleId,
            Message = f.Message,
            ElementId = id,
            ElementLabel = label,
            SeverityName = f.Severity.ToString(),
            SeverityGlyph = GlyphFor(f.Severity),
            SeverityBrush = BrushFor(f.Severity),
            SeverityRank = RankFor(f.Severity),
        };
    }

    private static string GlyphFor(ValidationSeverity s) => s switch
    {
        ValidationSeverity.Error   => "",   // ErrorBadge
        ValidationSeverity.Warning => "",   // Warning triangle
        ValidationSeverity.Info    => "",   // Info circle
        _                          => "",
    };

    private static Brush BrushFor(ValidationSeverity s) => s switch
    {
        ValidationSeverity.Error   => new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
        ValidationSeverity.Warning => new SolidColorBrush(Color.FromRgb(0xE0, 0x8E, 0x1F)),
        ValidationSeverity.Info    => new SolidColorBrush(Color.FromRgb(0x2A, 0x7A, 0xB8)),
        _                          => Brushes.Gray,
    };

    private static int RankFor(ValidationSeverity s) => s switch
    {
        ValidationSeverity.Error   => 0,
        ValidationSeverity.Warning => 1,
        ValidationSeverity.Info    => 2,
        _                          => 3,
    };

    /// <summary>
    /// Walk every IH in the document and return the first IE whose ID matches
    /// (with or without braces). Lookups are O(IE-count); the findings list is
    /// small enough that we refresh the labels on every validation run rather
    /// than maintain a side index.
    /// </summary>
    private static string? LookupName(CAEXDocument? doc, string id)
    {
        if (doc?.CAEXFile == null || string.IsNullOrEmpty(id)) return null;
        var bare = StripBraces(id);
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
        {
            foreach (var ie in WalkInternalElements(ih.InternalElement))
            {
                if (string.Equals(ie.ID, id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(StripBraces(ie.ID), bare, StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrEmpty(ie.Name) ? null : ie.Name;
                }
            }
        }
        return null;
    }

    private static IEnumerable<InternalElementType> WalkInternalElements(
        IEnumerable<InternalElementType> roots)
    {
        foreach (var ie in roots)
        {
            yield return ie;
            foreach (var child in WalkInternalElements(ie.InternalElement))
                yield return child;
        }
    }

    private static string StripBraces(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        return id.Length >= 2 && id[0] == '{' && id[^1] == '}' ? id[1..^1] : id;
    }
}
