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
    public void SelectElementCarriesTheId()
    {
        Assert.Equal("{\"type\":\"selectElement\",\"id\":\"po_1\"}", JsonSerializer.Serialize(SelectElementMessage.For("po_1")));
    }
}
