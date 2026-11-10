// Ctrl+S in the diagram: apply the changes, then save the AML file through the
// editor (016f E3, V22).

using Aml.Editor.Plugin.FPB.Views;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class SaveFlowTests
{
    private readonly List<string> _calls = new();

    private Func<ApplyResult> Apply(ApplyResult result) => () => { _calls.Add("apply"); return result; };
    private Func<bool> Save(bool ok) => () => { _calls.Add("save"); return ok; };

    [Fact]
    public void WithoutChangesItOnlySaves()
    {
        Assert.Equal(SaveOutcome.Saved, SaveFlow.Run(false, false, Apply(ApplyResult.Applied), Save(true)));
        Assert.Equal(new[] { "save" }, _calls);
    }

    [Fact]
    public void WithChangesItAppliesThenSaves()
    {
        Assert.Equal(SaveOutcome.AppliedAndSaved, SaveFlow.Run(true, false, Apply(ApplyResult.Applied), Save(true)));
        Assert.Equal(new[] { "apply", "save" }, _calls);
    }

    [Fact]
    public void ACancelledApplySavesNothing()
    {
        Assert.Equal(SaveOutcome.NotSaved, SaveFlow.Run(true, false, Apply(ApplyResult.Cancelled), Save(true)));
        Assert.Equal(new[] { "apply" }, _calls);
    }

    [Fact]
    public void AFailedApplySavesNothing()
    {
        Assert.Equal(SaveOutcome.ApplyFailed, SaveFlow.Run(true, false, Apply(ApplyResult.Failed), Save(true)));
        Assert.Equal(new[] { "apply" }, _calls);
    }

    [Fact]
    public void AFailedSaveSaysSo()
    {
        Assert.Equal(SaveOutcome.SaveFailed, SaveFlow.Run(true, false, Apply(ApplyResult.Applied), Save(false)));
    }

    [Fact]
    public void InAConflictNeitherAppliesNorSaves()
    {
        Assert.Equal(SaveOutcome.InConflict, SaveFlow.Run(true, true, Apply(ApplyResult.Applied), Save(true)));
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(SaveOutcome.AppliedAndSaved, "Applied and saved.")]
    [InlineData(SaveOutcome.Saved, "Saved.")]
    [InlineData(SaveOutcome.NotSaved, "Not saved.")]
    [InlineData(SaveOutcome.SaveFailed, "Applied. Saving failed, please save in the editor.")]
    [InlineData(SaveOutcome.InConflict, "The InstanceHierarchy was changed in the editor. Choose Apply diagram or Reload from AML first.")]
    public void TheStatusSaysWhatHappened(SaveOutcome outcome, string expected)
    {
        Assert.Equal(expected, SaveFlow.StatusText(outcome, null));
    }

    [Fact]
    public void AFailedApplyNamesTheReason()
    {
        Assert.Equal("Not saved: applying the diagram failed. boom", SaveFlow.StatusText(SaveOutcome.ApplyFailed, "boom"));
    }

    [Fact]
    public void TheEditorSaveNamesTheTabWithPendingChanges()
    {
        Assert.Equal("Saved without the diagram changes of 'Temperieren' that are not applied yet.", SaveFlow.SavedWithoutPendingText("Temperieren"));
    }
}
