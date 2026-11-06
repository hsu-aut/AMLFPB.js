using Aml.Editor.Plugin.FPB.Localization;
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

    /// <summary>The code of the rule in the catalog (031), else its id.</summary>
    public string Rule => CatalogueCodes.CodeOf(RuleId) ?? RuleId;
    public string Message { get; private init; } = "";
    public string ElementId { get; private init; } = "";
    public string ElementLabel { get; private init; } = "";

    /// <summary>The layer of the element (016f E3, V25), empty when the diagram does not show it.</summary>
    public string Layer { get; private init; } = "";

    /// <summary>The layer id of a finding of FPB.js.</summary>
    public string ProcessId { get; private init; } = "";

    /// <summary>A finding of FPB.js: <see cref="ElementId"/> is the id in the diagram, not in the AML.</summary>
    public bool FromDiagram { get; private init; }

    public string SeverityName { get; private init; } = "";
    public string SeverityGlyph { get; private init; } = "";
    public Brush SeverityBrush { get; private init; } = Brushes.Gray;
    public int SeverityRank { get; private init; }

    /// <summary>
    /// Build a row from a finding. <paramref name="doc"/> is used to look up a
    /// friendlier element label (IE name) — when it is null or the lookup
    /// misses we fall back to the bare id.
    /// </summary>
    public static FindingRow From(ValidationFinding f, CAEXDocument? doc, string? layer = null)
    {
        var id = f.TargetIeId ?? "";
        var label = string.IsNullOrEmpty(id) ? "" : (LookupName(doc, id) ?? id);

        return new FindingRow
        {
            RuleId = f.RuleId,
            // the plugin's own rules carry a template: shown in the plugin's language
            Message = f.Template != null ? L.T(f.Template, f.Args) : f.Message,
            // Brace-free: the double-click focus-jump feeds this to the FPB.js
            // viewer, whose element ids never carry the CAEX {…} wrapper — a
            // braced id made the jump miss silently for every mapper-built IE.
            ElementId = StripBraces(id),
            ElementLabel = label,
            Layer = layer ?? "",
            SeverityName = SeverityText(f.Severity),
            SeverityGlyph = GlyphFor(f.Severity),
            SeverityBrush = BrushFor(f.Severity),
            SeverityRank = RankFor(f.Severity),
        };
    }

    /// <summary>A row for a finding of FPB.js (016f E3, V25).</summary>
    public static FindingRow FromPage(PageFinding p, string? elementLabel, string? layer)
    {
        var severity = p.Severity switch
        {
            "error" => ValidationSeverity.Error,
            "warning" => ValidationSeverity.Warning,
            _ => ValidationSeverity.Info,
        };
        return new FindingRow
        {
            RuleId = p.Rule,
            Message = p.Message,
            ElementId = p.ElementId ?? "",
            ElementLabel = elementLabel ?? p.ElementId ?? "",
            Layer = layer ?? "",
            ProcessId = p.ProcessId ?? "",
            FromDiagram = true,
            SeverityName = SeverityText(severity),
            SeverityGlyph = GlyphFor(severity),
            SeverityBrush = BrushFor(severity),
            SeverityRank = RankFor(severity),
        };
    }

    private static string SeverityText(ValidationSeverity s) => s switch
    {
        ValidationSeverity.Error => L.T("Error"),
        ValidationSeverity.Warning => L.T("Warning"),
        _ => L.T("Info"),
    };

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
