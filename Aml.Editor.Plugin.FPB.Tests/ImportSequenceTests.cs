// Numbers of the imports, so a late answer of the page to an older import is
// dropped (016f E3, T18).

using Aml.Editor.Plugin.FPB.Bridge;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class ImportSequenceTests
{
    [Fact]
    public void NextCountsUp()
    {
        var sequence = new ImportSequence();
        Assert.Equal(1, sequence.Next());
        Assert.Equal(2, sequence.Next());
    }

    [Fact]
    public void OnlyTheLatestNumberIsCurrent()
    {
        var sequence = new ImportSequence();
        Assert.False(sequence.IsCurrent(0));
        sequence.Next();
        sequence.Next();
        Assert.False(sequence.IsCurrent(1));
        Assert.True(sequence.IsCurrent(2));
    }

    // 2.1.1: when the page refuses the latest import, its canvas shows the
    // model of an older one, and that answer is the one to take
    [Fact]
    public void AfterTheLatestImportIsRefusedTheAnswerToAnOlderOneCounts()
    {
        var sequence = new ImportSequence();
        sequence.Next();
        sequence.Next();
        sequence.Refused(2);
        Assert.True(sequence.IsCurrent(1));
        Assert.True(sequence.IsCurrent(2));

        sequence.Next();
        Assert.False(sequence.IsCurrent(1));
    }

    [Fact]
    public void ARefusalOfAnOlderImportChangesNothing()
    {
        var sequence = new ImportSequence();
        sequence.Next();
        sequence.Next();
        sequence.Refused(1);
        Assert.False(sequence.IsCurrent(1));
        Assert.True(sequence.IsCurrent(2));
    }
}
