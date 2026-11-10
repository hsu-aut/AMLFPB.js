// Which object of the InstanceHierarchy is which element on which layer of the
// diagram, for the selection between the AML tree and the diagram
// (016f E3, V26). The conversion of the mapper is the reference.

using System.Text.Json;
using Aml.Editor.Plugin.FPB.Bridge;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class FpdElementIndexTests
{
    private sealed record Loaded(InstanceHierarchyType Ih, JsonDocument Json, string Main, string Child);

    private static Loaded Load()
    {
        var json = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "Temperieren.json"));
        var doc = FpbJsonToCaex.Convert(json).Value;
        var ih = CaexToFpbJson.FindFpdInstanceHierarchies(doc).First();
        var back = JsonDocument.Parse(CaexToFpbJson.Convert(doc, ih).Value);
        var processes = back.RootElement.EnumerateArray().Skip(1).Select(e => e.GetProperty("process")).ToList();
        string? Decomposes(JsonElement p) =>
            p.TryGetProperty("isDecomposedProcessOperator", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var main = processes.First(p => string.IsNullOrEmpty(Decomposes(p))).GetProperty("id").GetString()!;
        var child = processes.First(p => !string.IsNullOrEmpty(Decomposes(p))).GetProperty("id").GetString()!;
        return new Loaded(ih, back, main, child);
    }

    /// <summary>(element id, layer id, type, name) of every element of every layer the conversion shows</summary>
    private static List<(string Id, string ProcessId, string Type, string? Name)> ElementsOf(JsonDocument json) =>
        json.RootElement.EnumerateArray().Skip(1).SelectMany(entry =>
        {
            var processId = entry.GetProperty("process").GetProperty("id").GetString()!;
            return entry.GetProperty("elementDataInformation").EnumerateArray().Select(e => (
                e.GetProperty("id").GetString()!,
                processId,
                e.GetProperty("$type").GetString()!,
                e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null));
        }).ToList();

    [Fact]
    public void EveryElementOfTheConversionLeadsToItsObjectAndBack()
    {
        var loaded = Load();
        var index = FpdElementIndex.Build(loaded.Ih);
        var elements = ElementsOf(loaded.Json);
        Assert.NotEmpty(elements);
        foreach (var (id, processId, type, _) in elements)
        {
            var caexId = index.IeIdFor(id, processId);
            Assert.True(caexId != null, $"{type} {id} on {processId} has no object");
            Assert.True(index.TryGetFpb(caexId!, out var fpbId, out var layer), $"{caexId} is not in the index");
            Assert.Equal((id, processId), (fpbId, layer));
        }
    }

    [Fact]
    public void ABoundaryStateHasOneObjectPerLayer()
    {
        var loaded = Load();
        var index = FpdElementIndex.Build(loaded.Ih);
        var elements = ElementsOf(loaded.Json);
        var states = new[] { FpbTypes.Product, FpbTypes.Energy, FpbTypes.Information };
        var boundary = elements
            .Where(e => states.Contains(e.Type))
            .GroupBy(e => e.Id)
            .First(g => g.Select(e => e.ProcessId).Distinct().Count() == 2)
            .Key;
        var above = index.IeIdFor(boundary, loaded.Main);
        var below = index.IeIdFor(boundary, loaded.Child);
        Assert.NotNull(above);
        Assert.NotNull(below);
        Assert.NotEqual(above, below);
    }

    [Fact]
    public void TheSubProcessIsTheLayerOfItsOperator()
    {
        var loaded = Load();
        var index = FpdElementIndex.Build(loaded.Ih);
        var processSuc = FpbMappings.ElementToSuc[FpbTypes.Process];
        var processIEs = loaded.Ih.InternalElement.Where(ie => FpbMappings.StripAlias(ie.RefBaseSystemUnitPath) == processSuc).ToList();
        var layers = processIEs.Select(ie => index.TryGetFpb(ie.ID, out var fpbId, out var layer) ? (fpbId, layer) : default).ToList();
        Assert.Contains((loaded.Main, loaded.Main), layers);
        Assert.Contains((loaded.Child, loaded.Child), layers);
        // the operator above that the layer below decomposes carries the same id
        var operatorAbove = ElementsOf(loaded.Json).Single(e => e.ProcessId == loaded.Main && e.Id == loaded.Child);
        Assert.Equal(FpbTypes.ProcessOperator, operatorAbove.Type);
        Assert.Equal(operatorAbove.Name, index.ProcessName(loaded.Child));
        Assert.False(string.IsNullOrEmpty(index.ProcessName(loaded.Main)));
    }

    [Fact]
    public void UnknownIdsGiveNothingAndBracesAreAccepted()
    {
        var loaded = Load();
        var index = FpdElementIndex.Build(loaded.Ih);
        Assert.False(index.TryGetFpb("no-such-object", out _, out _));
        Assert.Null(index.IeIdFor("no-such-element", loaded.Main));
        Assert.False(index.Contains("no-such-object"));
        var (id, processId, _, _) = ElementsOf(loaded.Json).First(e => e.Type == FpbTypes.ProcessOperator);
        var caexId = index.IeIdFor(id, processId)!;
        Assert.True(index.Contains("{" + caexId + "}"));
        Assert.True(index.TryGetFpb("{" + caexId + "}", out var fpbId, out _));
        Assert.Equal(id, fpbId);
    }
}
