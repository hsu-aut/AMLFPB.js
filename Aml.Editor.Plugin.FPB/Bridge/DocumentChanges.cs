using System.Xml.Linq;
using Aml.Engine.CAEX;

namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>
/// Whether anything in the AML document changed since the last look, from
/// the XLinq <see cref="XObject.Changed"/> event of the whole tree. The tick
/// of a tab looks for the links of the file (016f E4, T22) only after a
/// change: the search walks the whole document, about 76 ms on a 10 MB file.
/// A fresh watch counts as changed once.
/// </summary>
public sealed class DocumentChanges : IDisposable
{
    private readonly XDocument? _xml;
    private bool _changed = true;

    public DocumentChanges(CAEXDocument doc)
    {
        _xml = doc.XDocument;
        if (_xml != null) _xml.Changed += OnChanged;
    }

    private void OnChanged(object? sender, XObjectChangeEventArgs e) => _changed = true;

    /// <summary>True when the document changed since the last call; resets the mark.</summary>
    public bool TakeChanged()
    {
        var changed = _changed;
        _changed = false;
        return changed;
    }

    public void Dispose()
    {
        if (_xml != null) _xml.Changed -= OnChanged;
    }
}
