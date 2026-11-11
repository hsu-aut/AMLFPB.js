namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>
/// Numbers of the imports into the page (016f E3, T18). The page answers an
/// import with its number; an answer to an older import arrives late and
/// describes a model that is no longer the one sent last, so it is dropped.
/// </summary>
public sealed class ImportSequence
{
    private int _current;

    // the page refused the import sent last: its canvas shows the model of an
    // older import, whose answer may still come and then counts (2.1.1)
    private bool _currentRefused;

    public int Next()
    {
        _currentRefused = false;
        return ++_current;
    }

    /// <summary>The page refused import <paramref name="seq"/>.</summary>
    public void Refused(int seq)
    {
        if (seq > 0 && seq == _current) _currentRefused = true;
    }

    public bool IsCurrent(int seq) => seq > 0 && (seq == _current || (_currentRefused && seq < _current));
}
