using System.Text.Json;

namespace TrayX.Contracts;

public sealed class IpcEnvelope
{
    public string Type { get; init; } = string.Empty;

    public Guid? Id { get; init; }

    public string? Name { get; init; }

    public JsonElement? Payload { get; init; }

    public bool? Success { get; init; }

    public string? Error { get; init; }

    public static IpcEnvelope Request(Guid id, string name, object? payload = null) => new()
    {
        Type = IpcMessageTypes.Request,
        Id = id,
        Name = name,
        Payload = IpcJson.ToElement(payload)
    };

    public static IpcEnvelope Response(Guid id, object? payload = null) => new()
    {
        Type = IpcMessageTypes.Response,
        Id = id,
        Success = true,
        Payload = IpcJson.ToElement(payload)
    };

    public static IpcEnvelope Failure(Guid id, string error) => new()
    {
        Type = IpcMessageTypes.Response,
        Id = id,
        Success = false,
        Error = error
    };

    public static IpcEnvelope Event(string name, object? payload = null) => new()
    {
        Type = IpcMessageTypes.Event,
        Name = name,
        Payload = IpcJson.ToElement(payload)
    };
}

public static class IpcMessageTypes
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };

    public static JsonElement? ToElement(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return element.Value.Deserialize<T>(Options);
    }

    public static string Serialize(IpcEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    public static IpcEnvelope? Deserialize(string line) =>
        JsonSerializer.Deserialize<IpcEnvelope>(line, Options);
}

public sealed class IpcException : Exception
{
    public IpcException(string message) : base(message)
    {
    }

    public IpcException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
