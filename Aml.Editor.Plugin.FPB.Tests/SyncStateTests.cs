// What a tab does when the InstanceHierarchy changes in the editor while the
// diagram has changes that are not applied (016f E3, V28).

using Aml.Editor.Plugin.FPB.Views;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class SyncStateTests
{
    private static SyncState Pending()
    {
        var state = new SyncState();
        state.DiagramChanged(equalsBaseline: false);
        return state;
    }

    private static SyncState Conflict()
    {
        var state = Pending();
        state.TreeChanged();
        return state;
    }

    [Fact]
    public void ACleanTabReloadsQuietly()
    {
        var state = new SyncState();
        Assert.Equal(TreeChangeAction.Reload, state.TreeChanged());
        Assert.Equal(SyncPhase.Clean, state.Phase);
        Assert.False(state.BarVisible);
    }

    [Fact]
    public void ATabWithChangesShowsTheBarInsteadOfDroppingThem()
    {
        var state = Pending();
        Assert.Equal(TreeChangeAction.ShowBar, state.TreeChanged());
        Assert.Equal(SyncPhase.Conflict, state.Phase);
        Assert.True(state.BarVisible);
    }

    [Fact]
    public void WhileTheBarStandsNothingReloads()
    {
        var state = Conflict();
        Assert.Equal(TreeChangeAction.None, state.TreeChanged());
        state.DiagramChanged(equalsBaseline: false);
        Assert.Equal(SyncPhase.Conflict, state.Phase);
        Assert.Equal(TreeChangeAction.None, state.TreeChanged());
    }

    [Fact]
    public void UndoingTheDiagramBackDoesNotEndTheConflict()
    {
        // the tree is still not what the diagram was loaded from
        var state = Conflict();
        state.DiagramChanged(equalsBaseline: true);
        Assert.Equal(SyncPhase.Conflict, state.Phase);
        Assert.True(state.BarVisible);
    }

    [Fact]
    public void ApplyingOrReloadingEndsTheConflict()
    {
        var applied = Conflict();
        applied.Applied();
        Assert.Equal(SyncPhase.Clean, applied.Phase);
        Assert.False(applied.BarVisible);

        var reloaded = Conflict();
        reloaded.Reloaded();
        Assert.Equal(SyncPhase.Clean, reloaded.Phase);
        Assert.False(reloaded.BarVisible);
    }

    [Fact]
    public void BackToTheBaselineMeansNothingPending()
    {
        var state = Pending();
        state.DiagramChanged(equalsBaseline: true);
        Assert.Equal(SyncPhase.Clean, state.Phase);
    }
}
