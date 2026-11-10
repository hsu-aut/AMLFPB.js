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
}
