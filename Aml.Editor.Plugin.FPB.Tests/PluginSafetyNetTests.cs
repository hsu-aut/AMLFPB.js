// An exception from the plugin's own code must not end the editor (016f E3,
// T19): the safety net takes exceptions whose stack passes through the
// plugin, judged by the assembly of each frame.

using Aml.Editor.Plugin.FPB.Bridge;
using Aml.Editor.Plugin.FPB.Diagnostics;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class PluginSafetyNetTests
{
    private static Exception Caught(Action action)
    {
        try { action(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("nothing was thrown");
    }

    [Fact]
    public void AnExceptionThrownInThePluginIsOurs()
    {
        var ex = Caught(() => FpdElementIndex.Build(null!));
        Assert.True(PluginSafetyNet.IsOurs(ex));
    }

    [Fact]
    public void AnExceptionWithoutAFrameOfThePluginIsNot()
    {
        var ex = Caught(() => Array.Empty<int>().First());
        Assert.False(PluginSafetyNet.IsOurs(ex));
    }

    [Fact]
    public void AForeignExceptionAroundOneOfThePluginIsOurs()
    {
        var inner = Caught(() => FpdElementIndex.Build(null!));
        var outer = Caught(() => throw new InvalidOperationException("outer", inner));
        Assert.True(PluginSafetyNet.IsOurs(outer));
    }
}
