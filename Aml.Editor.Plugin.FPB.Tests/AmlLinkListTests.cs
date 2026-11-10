// The links of the AML file the page's delete question names, per layer and
// element of the diagram (016f E4, T22): ForeignLinks of the mapper gives them
// per InternalElement, the index of the selection (E3, T17) places them.

using Aml.Editor.Plugin.FPB.Bridge;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class AmlLinkListTests
{
    private sealed record Model(CAEXDocument Doc, InstanceHierarchyType Ih, InternalElementType Top, InternalElementType Sub);

    private static Model Load()
    {
        var json = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "Temperieren.json"));
        var doc = FpbJsonToCaex.Convert(json).Value;
        var ih = CaexToFpbJson.FindFpdInstanceHierarchies(doc).First();
        var top = ih.InternalElement.First(ie => string.IsNullOrEmpty(ie.GetRefObjOrDerived()));
        var sub = ih.InternalElement.First(ie => !string.IsNullOrEmpty(ie.GetRefObjOrDerived()));
        return new Model(doc, ih, top, sub);
    }

    private static string Bare(string? id) => (id ?? "").Trim().TrimStart('{').TrimEnd('}');

    private static ElementLinks LinksAt(InternalElementType ie, params ForeignLink[] links) =>
        new(Bare(ie.ID), ie.Name ?? "", links);

    [Fact]
    public void ACopyOfABoundaryStateLandsOnItsLayer()
    {
        var m = Load();
        var copy = m.Sub.InternalElement.First(ie => ie.Name == "Warmprodukt");
        var state = m.Top.InternalElement.First(ie => ie.Name == "Warmprodukt");
        var layer = Bare(m.Sub.GetRefObjOrDerived());

        var list = AmlLinkList.For(new[] { LinksAt(copy, new ForeignLink("L1", "Tank")) }, FpdElementIndex.Build(m.Ih));

        Assert.Equal(new[] { new AmlLinkEntry(layer, Bare(state.ID), "Warmprodukt", "L1", "Tank") }, list);
    }

    [Fact]
    public void TheProcessOfALowerLayerIsItsOwnElement()
    {
        var m = Load();
        var layer = Bare(m.Sub.GetRefObjOrDerived());

        var list = AmlLinkList.For(new[] { LinksAt(m.Sub, new ForeignLink("L1", "Tank")) }, FpdElementIndex.Build(m.Ih));

        Assert.Equal(new[] { new AmlLinkEntry(layer, layer, m.Sub.Name ?? "", "L1", "Tank") }, list);
    }

    [Fact]
    public void AnObjectTheDiagramDoesNotShowIsLeftOut()
    {
        var m = Load();
        var unknown = new ElementLinks("00000000-0000-4000-8000-00000000beef", "Elsewhere", new[] { new ForeignLink("L1", "Tank") });

        Assert.Empty(AmlLinkList.For(new[] { unknown }, FpdElementIndex.Build(m.Ih)));
    }

    [Fact]
    public void TheKeyIgnoresTheOrderOfTheInput()
    {
        var a = new AmlLinkEntry("p", "e1", "A", "L1", "Tank");
        var b = new AmlLinkEntry("p", "e2", "B", "L2", "Tank");

        Assert.Equal(AmlLinkList.Key(new[] { a, b }), AmlLinkList.Key(new[] { b, a }));
    }

    [Fact]
    public void TheKeyChangesWhenALinkChanges()
    {
        var a = new AmlLinkEntry("p", "e1", "A", "L1", "Tank");

        Assert.NotEqual(AmlLinkList.Key(new[] { a }), AmlLinkList.Key(new[] { a with { Owner = "Pump" } }));
        Assert.NotEqual(AmlLinkList.Key(new[] { a }), AmlLinkList.Key(Array.Empty<AmlLinkEntry>()));
    }

    [Fact]
    public void TheLinksOfAFileFollowFromFind()
    {
        var m = Load();
        var scrap = m.Top.InternalElement.First(ie => ie.Name == "Ausschuss");
        var tank = m.Doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Tank");
        var link = tank.InternalLink.Append("TankToScrap");
        link.AInterface = tank.ExternalInterface.Append("ToScrap");
        link.BInterface = scrap.ExternalInterface.Append("PlantLink");
        var index = FpdElementIndex.Build(m.Ih);
        Assert.True(index.TryGetFpb(scrap.ID, out _, out var layer));

        var list = AmlLinkList.For(ForeignLinks.Find(m.Doc, m.Ih), index);

        Assert.Equal(new[] { new AmlLinkEntry(layer, Bare(scrap.ID), "Ausschuss", "TankToScrap", "Tank") }, list);
    }
}
