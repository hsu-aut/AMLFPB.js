// Which document a selection from the editor belongs to (DocumentIdentity).
//
// The trees of external libraries report selections with objects of other
// CAEXDocuments; the viewer must not switch to those.

using Aml.Editor.Plugin.FPB.Bridge;
using Aml.Engine.CAEX;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class DocumentIdentityTests
{
    private static CAEXDocument NewDocument() => CAEXDocument.New_CAEXDocument();

    private static Func<CAEXDocument?> Editor(CAEXDocument? document) => () => document;

    [Fact]
    public void ASelectionInTheShownDocumentIsFollowedWithoutAskingTheEditor()
    {
        var shown = NewDocument();
        var ih = shown.CAEXFile.InstanceHierarchy.Append("Plant");

        var asked = false;
        Assert.True(DocumentIdentity.IsOpenDocument(ih.CAEXDocument, shown, () => { asked = true; return null; }));
        Assert.False(asked);
    }

    [Fact]
    public void ASelectionInAnExternalLibraryIsIgnored()
    {
        var shown = NewDocument();
        var library = NewDocument();

        Assert.False(DocumentIdentity.IsOpenDocument(library, shown, Editor(shown)));
    }

    [Fact]
    public void ASelectionInTheDocumentTheEditorOpenedSinceIsFollowed()
    {
        var shown = NewDocument();
        var opened = NewDocument();

        Assert.True(DocumentIdentity.IsOpenDocument(opened, shown, Editor(opened)));
    }

    [Fact]
    public void BeforeTheFirstDocumentALibrarySelectionIsIgnoredWhenTheEditorDocumentIsKnown()
    {
        var open = NewDocument();
        var library = NewDocument();

        Assert.False(DocumentIdentity.IsOpenDocument(library, null, Editor(open)));
        Assert.True(DocumentIdentity.IsOpenDocument(open, null, Editor(open)));
    }

    [Fact]
    public void WithoutTheEditorDocumentOnlyAnEmptyViewerFollowsAnotherDocument()
    {
        var shown = NewDocument();
        var other = NewDocument();

        Assert.False(DocumentIdentity.IsOpenDocument(other, shown, Editor(null)));
        Assert.True(DocumentIdentity.IsOpenDocument(other, null, Editor(null)));
    }

    [Fact]
    public void SameDocumentComparesTheXmlDocumentAndToleratesNull()
    {
        var a = NewDocument();
        var b = NewDocument();

        Assert.True(DocumentIdentity.SameDocument(a, a));
        Assert.False(DocumentIdentity.SameDocument(a, b));
        Assert.False(DocumentIdentity.SameDocument(a, null));
        Assert.False(DocumentIdentity.SameDocument(null, null));
    }
}
