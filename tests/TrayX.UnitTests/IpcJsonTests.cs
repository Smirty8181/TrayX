using System.Text.Json;
using TrayX.Contracts;

namespace TrayX.UnitTests;

public sealed class IpcJsonTests
{
    [Fact]
    public void Deserialize_RejectsPayloadsBeyondMaximumDepth()
    {
        var nestedPayload = new string('[', 32) + "0" + new string(']', 32);
        var json = $"{{\"type\":\"request\",\"payload\":{nestedPayload}}}";

        Assert.Throws<JsonException>(() => IpcJson.Deserialize(json));
    }
}
