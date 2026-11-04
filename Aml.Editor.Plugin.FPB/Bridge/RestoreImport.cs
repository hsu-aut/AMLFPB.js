namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>
/// Pending edits restored after a tab was rebuilt (a document switch) are
/// shown in the page as well, while the AML stays the baseline: the AML is
/// imported first and its answer is the baseline, then the restored edits,
/// whose answer is not. Before (final review of 016f E3) the page showed the
/// AML only, so Ctrl+S sent the AML state, which equalled the baseline, and
/// the restored edits were dropped as "nothing pending".
/// </summary>
public sealed class RestoreImport
{
    private string? _waiting;
    private bool _skipBaseline;

    /// <summary>Restore <paramref name="snapshot"/>; returns it when it is to be imported at once (the AML was answered already).</summary>
    public string? Restore(string snapshot, bool answeredAlready)
    {
        if (answeredAlready)
        {
            _waiting = null;
            _skipBaseline = true;
            return snapshot;
        }
        _waiting = snapshot;
        return null;
    }

    /// <summary>The page answered an import: whether its model is the baseline, and what to import next.</summary>
    public (bool UseAsBaseline, string? ImportNext) Answered()
    {
        if (_skipBaseline)
        {
            _skipBaseline = false;
            return (false, null);
        }
        if (_waiting is { } snapshot)
        {
            _waiting = null;
            _skipBaseline = true;
            return (true, snapshot);
        }
        return (true, null);
    }

    /// <summary>The diagram is loaded from the AML again; a restore still waiting is dropped.</summary>
    public void Reset()
    {
        _waiting = null;
        _skipBaseline = false;
    }
}
