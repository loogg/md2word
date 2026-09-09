using System.Text.Json;
using System.Text.Json.Serialization;

namespace Md2Word.Worker.Protocol;

internal sealed class ProtocolWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TextWriter output;
    private readonly object gate = new();

    public ProtocolWriter(TextWriter output)
    {
        this.output = output;
    }

    public void Event(string requestId, ConversionEvent conversionEvent) =>
        Write(new { protocolVersion = ProtocolConstants.Version, requestId, type = "event", @event = conversionEvent });

    public void Result(string requestId, object result) =>
        Write(new { protocolVersion = ProtocolConstants.Version, requestId, type = "result", result });

    public void Error(string requestId, WorkerError error) =>
        Write(new { protocolVersion = ProtocolConstants.Version, requestId, type = "error", error });

    private void Write(object frame)
    {
        var json = JsonSerializer.Serialize(frame, SerializerOptions);
        lock (gate)
        {
            output.WriteLine(json);
            output.Flush();
        }
    }
}
