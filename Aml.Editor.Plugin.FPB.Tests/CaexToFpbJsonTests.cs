using System.Text.Json;
using FpbMapper.Conversion;
using FpbMapper.Conversion.Models;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class CaexToFpbJsonTests
{
    private static string GetTestDataPath(string filename) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", filename);

    [Fact]
    public void Roundtrip_JsonToCaexToJson_PreservesStructure()
    {
        var originalJson = File.ReadAllText(GetTestDataPath("Temperieren.json"));
        var document = FpbJsonToCaex.Convert(originalJson).Value;
        var resultJson = CaexToFpbJson.Convert(document).Value;

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() >= 2, "Should have at least project header + 1 process entry");

        var projectEl = root[0];
        Assert.Equal("fpb:Project", projectEl.GetProperty("$type").GetString());
        Assert.NotEmpty(projectEl.GetProperty("entryPoint").GetString()!);
    }

    [Fact]
    public void Roundtrip_PreservesElementCounts()
    {
        var originalJson = File.ReadAllText(GetTestDataPath("Temperieren.json"));
        var (_, originalEntries) = FpbJsonParser.Parse(originalJson);

        var originalProcessCount = originalEntries.Count;
        var originalElementCount = originalEntries.SelectMany(e => e.ElementData).Count();

        var document = FpbJsonToCaex.Convert(originalJson).Value;
        var resultJson = CaexToFpbJson.Convert(document).Value;
        var (_, resultEntries) = FpbJsonParser.Parse(resultJson);

        Assert.Equal(originalProcessCount, resultEntries.Count);

        var resultElementCount = resultEntries.SelectMany(e => e.ElementData).Count();
        Assert.Equal(originalElementCount, resultElementCount);
    }

    [Fact]
    public void Roundtrip_PreservesFlowConnections()
    {
        var originalJson = File.ReadAllText(GetTestDataPath("Temperieren.json"));
        var (_, originalEntries) = FpbJsonParser.Parse(originalJson);

        var originalFlowCount = originalEntries
            .SelectMany(e => e.ElementData)
            .Count(e => FpbMappings.ConnectionTypes.Contains(e.Type));

        var document = FpbJsonToCaex.Convert(originalJson).Value;
        var resultJson = CaexToFpbJson.Convert(document).Value;
        var (_, resultEntries) = FpbJsonParser.Parse(resultJson);

        var resultFlowCount = resultEntries
            .SelectMany(e => e.ElementData)
            .Count(e => FpbMappings.ConnectionTypes.Contains(e.Type));

        Assert.Equal(originalFlowCount, resultFlowCount);
    }

    [Fact]
    public void Roundtrip_PreservesVisualInformation()
    {
        var originalJson = File.ReadAllText(GetTestDataPath("Temperieren.json"));
        var (_, originalEntries) = FpbJsonParser.Parse(originalJson);

        var originalVisualCount = originalEntries.SelectMany(e => e.ElementVisual).Count();

        var document = FpbJsonToCaex.Convert(originalJson).Value;
        var resultJson = CaexToFpbJson.Convert(document).Value;
        var (_, resultEntries) = FpbJsonParser.Parse(resultJson);

        var resultVisualCount = resultEntries.SelectMany(e => e.ElementVisual).Count();

        Assert.Equal(originalVisualCount, resultVisualCount);
    }
}
