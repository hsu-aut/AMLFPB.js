namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>
/// Numbers of the imports into the page (016f E3, T18). The page answers an
/// import with its number; an answer to an older import arrives late and
/// describes a model that is no longer the one sent last, so it is dropped.
/// </summary>
public sealed class ImportSequence
{
    private int _current;

    public int Next() => ++_current;

    public bool IsCurrent(int seq) => seq > 0 && seq == _current;
}
