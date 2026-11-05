// Host → page messages as fpbjs-assets/index.html reads them.

using System.Text.Json;
using Aml.Editor.Plugin.FPB.Bridge;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class BridgeMessageTests
{
    [Theory]
    [InlineData(true, "{\"type\":\"setReadOnly\",\"readOnly\":true}")]
    [InlineData(false, "{\"type\":\"setReadOnly\",\"readOnly\":false}")]
    public void SetReadOnlyCarriesTheTypeAndTheFlagThePageReads(bool readOnly, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(SetReadOnlyMessage.For(readOnly)));
    }

    [Fact]
    public void SelectCarriesTheIdAndTheLayer()
    {
        Assert.Equal("{\"type\":\"select\",\"id\":\"po_1\",\"processId\":\"p_1\"}", JsonSerializer.Serialize(SelectMessage.For("po_1", "p_1")));
    }

    [Fact]
    public void SelectWithoutLayerCarriesNull()
    {
        Assert.Equal("{\"type\":\"select\",\"id\":\"po_1\",\"processId\":null}", JsonSerializer.Serialize(SelectMessage.For("po_1", null)));
    }

    [Fact]
    public void SetValidationMarkersCarriesTheFlag()
    {
        Assert.Equal("{\"type\":\"setValidationMarkers\",\"show\":false}", JsonSerializer.Serialize(SetValidationMarkersMessage.For(false)));
    }

    [Fact]
    public void SetAmlLinksSerializesLikeTheLibraryExpectsIt()
    {
        Assert.Equal(
            "{\"type\":\"setAmlLinks\",\"links\":[{\"processId\":\"p\",\"elementId\":\"e\",\"name\":\"Heizen\",\"link\":\"L1\",\"owner\":\"Plant\"}]}",
            JsonSerializer.Serialize(SetAmlLinksMessage.For(new[] { new AmlLinkEntry("p", "e", "Heizen", "L1", "Plant") })));
    }

    [Fact]
    public void ImportJsonCarriesItsNumberAndTheModel()
    {
        using var model = JsonDocument.Parse("[1]");
        Assert.Equal("{\"type\":\"importJSON\",\"seq\":3,\"data\":[1]}", JsonSerializer.Serialize(ImportJsonMessage.For(3, model.RootElement)));
    }
}
