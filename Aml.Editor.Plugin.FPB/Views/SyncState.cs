namespace Aml.Editor.Plugin.FPB.Views;

/// <summary>Where a tab stands between its diagram and its InstanceHierarchy.</summary>
public enum SyncPhase
{
    /// <summary>The diagram shows what the InstanceHierarchy holds.</summary>
    Clean,
    /// <summary>The diagram has changes that are not applied ("●" at the tab).</summary>
    Pending,
    /// <summary>The InstanceHierarchy changed in the editor while changes were pending (the bar).</summary>
    Conflict,
}

/// <summary>What the tab does after the InstanceHierarchy changed in the editor.</summary>
public enum TreeChangeAction
{
    /// <summary>Load the diagram from the AML again, quietly.</summary>
    Reload,
    /// <summary>Keep the diagram and show the bar with "Apply diagram" and "Reload from AML".</summary>
    ShowBar,
    /// <summary>The bar stands already; nothing to do.</summary>
    None,
}

/// <summary>
/// The rules of 016f E3, V28: a change of the InstanceHierarchy in the editor
/// never drops changes of the diagram. Without pending changes the diagram
/// reloads quietly; with them the bar asks, and until the user chooses
/// nothing reloads, whatever else changes on either side.
/// </summary>
public sealed class SyncState
{
    public SyncPhase Phase { get; private set; } = SyncPhase.Clean;

    public bool BarVisible => Phase == SyncPhase.Conflict;

    /// <summary>The diagram changed; <paramref name="equalsBaseline"/> when it is back at what was loaded or applied last.</summary>
    public void DiagramChanged(bool equalsBaseline)
    {
        // in a conflict the tree differs from the diagram either way
        if (Phase == SyncPhase.Conflict) return;
        Phase = equalsBaseline ? SyncPhase.Clean : SyncPhase.Pending;
    }

    public TreeChangeAction TreeChanged()
    {
        switch (Phase)
        {
            case SyncPhase.Clean:
                return TreeChangeAction.Reload;
            case SyncPhase.Pending:
                Phase = SyncPhase.Conflict;
                return TreeChangeAction.ShowBar;
            default:
                return TreeChangeAction.None;
        }
    }

    /// <summary>The diagram was written into the InstanceHierarchy.</summary>
    public void Applied() => Phase = SyncPhase.Clean;

    /// <summary>The diagram was loaded from the AML again; its changes are gone.</summary>
    public void Reloaded() => Phase = SyncPhase.Clean;

    /// <summary>
    /// What stays pending after <paramref name="applied"/> was written into the
    /// InstanceHierarchy: a newer state of the diagram that arrived meanwhile
    /// (the questions before applying pump the dispatcher), else nothing.
    /// </summary>
    public static string? PendingAfterApply(string applied, string? current) =>
        current == null || string.Equals(current, applied, StringComparison.Ordinal) ? null : current;
}
