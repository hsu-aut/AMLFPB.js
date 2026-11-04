namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>
/// Which selection of the diagram goes on to the AML tree (016f E3, V26). The
/// page answers every selection, also one the tree caused; that answer must
/// not go back. Every other selection of the diagram goes to the tree, also
/// one of an object it went to before, once the tree selected something else
/// in between (a library class, another hierarchy).
/// </summary>
public sealed class SelectionEcho
{
    private string? _last;

    /// <summary>The tree selected something; <paramref name="shownId"/> is the object the diagram now shows, or null.</summary>
    public void FromTree(string? shownId) => _last = shownId;

    /// <summary>The diagram selected an object; true when it goes to the tree.</summary>
    public bool ToTree(string objectId)
    {
        if (string.Equals(objectId, _last, StringComparison.OrdinalIgnoreCase)) return false;
        _last = objectId;
        return true;
    }
}
