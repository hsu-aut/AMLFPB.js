// Pending edits restored after a tab rebuild are shown in the page, while the
// AML stays the baseline (final review of 016f E3: Ctrl+S used to drop them).

using Aml.Editor.Plugin.FPB.Bridge;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class RestoreImportTests
{
    [Fact]
    public void WithoutARestoreEveryAnswerIsTheBaseline()
    {
        var restore = new RestoreImport();
        Assert.Equal((true, (string?)null), restore.Answered());
        Assert.Equal((true, (string?)null), restore.Answered());
    }

    [Fact]
    public void ARestoreBeforeTheAmlIsAnsweredFollowsItAndIsNoBaseline()
    {
        var restore = new RestoreImport();
        Assert.Null(restore.Restore("edits", answeredAlready: false));
        Assert.Equal((true, (string?)"edits"), restore.Answered());   // the AML: baseline, then the edits
        Assert.Equal((false, (string?)null), restore.Answered());     // the edits: no baseline
        Assert.Equal((true, (string?)null), restore.Answered());      // later imports as usual
    }

    [Fact]
    public void ARestoreAfterTheAmlWasAnsweredIsImportedAtOnce()
    {
        var restore = new RestoreImport();
        Assert.Equal("edits", restore.Restore("edits", answeredAlready: true));
        Assert.Equal((false, (string?)null), restore.Answered());
        Assert.Equal((true, (string?)null), restore.Answered());
    }

    [Fact]
    public void AReloadFromTheAmlForgetsTheRestore()
    {
        var restore = new RestoreImport();
        restore.Restore("edits", answeredAlready: false);
        restore.Reset();
        Assert.Equal((true, (string?)null), restore.Answered());
    }
}
