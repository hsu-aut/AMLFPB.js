// The tick looks for the links of the file only when the document changed
// (016f E4, risk "Kosten des Takts"): on a 10 MB file the search takes about
// 76 ms, every 2 s and per tab, on the UI thread.

using Aml.Editor.Plugin.FPB.Bridge;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class DocumentChangesTests
{
    private static CAEXDocument Temperieren()
    {
        var json = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "Temperieren.json"));
        return FpbJsonToCaex.Convert(json).Value;
    }

    [Fact]
    public void AFreshWatchCountsAsChangedOnce()
    {
        using var changes = new DocumentChanges(Temperieren());

        Assert.True(changes.TakeChanged());
        Assert.False(changes.TakeChanged());
    }

    [Fact]
    public void ANewElementAnywhereInTheFileIsAChange()
    {
        var doc = Temperieren();
        using var changes = new DocumentChanges(doc);
        changes.TakeChanged();

        doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Tank");

        Assert.True(changes.TakeChanged());
        Assert.False(changes.TakeChanged());
    }

    [Fact]
    public void ANewLinkIsAChange()
    {
        var doc = Temperieren();
        var tank = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Tank");
        var a = tank.ExternalInterface.Append("A");
        var b = tank.InternalElement.Append("Sensor").ExternalInterface.Append("B");
        using var changes = new DocumentChanges(doc);
        changes.TakeChanged();

        var link = tank.InternalLink.Append("AToB");
        link.AInterface = a;
        link.BInterface = b;

        Assert.True(changes.TakeChanged());
    }

    [Fact]
    public void AfterDisposeChangesAreNoLongerWatched()
    {
        var doc = Temperieren();
        var changes = new DocumentChanges(doc);
        changes.TakeChanged();
        changes.Dispose();

        doc.CAEXFile.InstanceHierarchy.Append("Plant");

        Assert.False(changes.TakeChanged());
    }
}
