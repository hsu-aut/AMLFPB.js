// Which document a selection from the editor belongs to.
//
// The editor reports selections from all of its trees, the trees of external
// libraries included, and their objects belong to other CAEXDocuments (read in
// the decompiled editor 6.4.3.5). Following such a selection switched the
// viewer to the library: its tabs were rebuilt for a document without FPD
// hierarchies. The viewer follows a selection into another document only when
// that document is the one the editor has open.

using Aml.Engine.CAEX;

namespace Aml.Editor.Plugin.FPB.Bridge;

public static class DocumentIdentity
{
    /// <summary>
    /// True when <paramref name="candidate"/> is the document the editor has
    /// open. <paramref name="findEditorDocument"/> reads the editor's main view
    /// model (<see cref="DocumentDiscoverer"/>) and is only asked when the
    /// candidate is not the document shown already. When it finds nothing, a
    /// viewer that shows no document yet follows the selection, as before.
    /// </summary>
    public static bool IsOpenDocument(CAEXDocument candidate, CAEXDocument? shown, Func<CAEXDocument?> findEditorDocument)
    {
        if (SameDocument(candidate, shown)) return true;
        var editorDocument = findEditorDocument();
        if (editorDocument != null) return SameDocument(candidate, editorDocument);
        return shown == null;
    }

    /// <summary>The same wrapper, or two wrappers around the same XML document.</summary>
    public static bool SameDocument(CAEXDocument? a, CAEXDocument? b)
    {
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        var xml = a.CAEXFile?.Node?.Document;
        return xml != null && ReferenceEquals(xml, b.CAEXFile?.Node?.Document);
    }
}
