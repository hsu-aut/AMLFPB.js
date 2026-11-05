using Aml.Editor.Plugin.FPB.Localization;
namespace Aml.Editor.Plugin.FPB.Views;

/// <summary>What became of applying the diagram to the InstanceHierarchy.</summary>
public enum ApplyResult
{
    Applied,
    /// <summary>The user said no to one of the questions before applying.</summary>
    Cancelled,
    Failed,
}

/// <summary>What Ctrl+S in the diagram did (016f E3, V22).</summary>
public enum SaveOutcome
{
    AppliedAndSaved,
    /// <summary>Nothing to apply; the file was saved.</summary>
    Saved,
    /// <summary>Applying was cancelled; nothing was written.</summary>
    NotSaved,
    ApplyFailed,
    /// <summary>Applied, but the editor's save could not be invoked.</summary>
    SaveFailed,
    /// <summary>The bar of a conflict stands (V28): neither applied nor saved.</summary>
    InConflict,
}

/// <summary>
/// Ctrl+S in the diagram (016f E3, V22): apply what the diagram has that the
/// InstanceHierarchy does not, the same way as "Update InstanceHierarchy",
/// and then save the AML file through the editor, whatever the setting
/// AutoSaveAfterUpdate says. Nothing is saved when applying did not go
/// through, and nothing happens while the bar of a conflict stands.
/// </summary>
public static class SaveFlow
{
    public static SaveOutcome Run(bool hasChanges, bool inConflict, Func<ApplyResult> apply, Func<bool> save)
    {
        if (inConflict) return SaveOutcome.InConflict;
        if (hasChanges)
        {
            switch (apply())
            {
                case ApplyResult.Cancelled: return SaveOutcome.NotSaved;
                case ApplyResult.Failed: return SaveOutcome.ApplyFailed;
            }
            return save() ? SaveOutcome.AppliedAndSaved : SaveOutcome.SaveFailed;
        }
        return save() ? SaveOutcome.Saved : SaveOutcome.SaveFailed;
    }

    public static string StatusText(SaveOutcome outcome, string? reason) => outcome switch
    {
        SaveOutcome.AppliedAndSaved => L.T("Applied and saved."),
        SaveOutcome.Saved => L.T("Saved."),
        SaveOutcome.NotSaved => L.T("Not saved."),
        SaveOutcome.ApplyFailed => L.T("Not saved: applying the diagram failed. {0}", reason ?? ""),
        SaveOutcome.SaveFailed => L.T("Applied. Saving failed, please save in the editor."),
        SaveOutcome.InConflict => L.T("The InstanceHierarchy was changed in the editor. Choose Apply diagram or Reload from AML first."),
        _ => "",
    };

    /// <summary>The status of a tab whose changes are not in a file the editor just saved (V23).</summary>
    public static string SavedWithoutPendingText(string tab) =>
        L.T("Saved without the diagram changes of '{0}' that are not applied yet.", tab);
}
