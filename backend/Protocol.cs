using System.Text.Json;
using System.Text.Json.Nodes;

namespace LmStudioBackend;

/// <summary>
/// NDJSON stdio protocol between the VS Code TypeScript shell and this backend.
///
/// Shell -> backend (one JSON object per line on stdin):
///   { "id": "...", "method": "...", "params": { ... } }
///   { "id": "...", "method": "cancel" }   // cancels an in-flight request with that id (e.g. chatStream)
///
/// Backend -> shell (one JSON object per line on stdout):
///   { "id": "...", "ok": true, "result": ... }              // non-streaming success
///   { "id": "...", "ok": false, "error": "message" }        // non-streaming failure
///   { "id": "...", "event": "chunk", "part": { ... } }      // streaming chunk (chatStream)
///   { "id": "...", "event": "end" }                          // streaming completed
///   { "id": "...", "event": "error", "message": "..." }      // streaming failed
///   { "event": "log", "level": "verbose|info|warning|error", "message": "..." } // no id: one-way log line
/// </summary>
public static class Protocol
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class RequestEnvelope
{
    public string Id { get; set; } = "";
    public string Method { get; set; } = "";
    public JsonNode? Params { get; set; }
}

/// <summary>Serializes stdout messages one at a time under a lock so concurrent streaming requests can't interleave partial JSON lines.</summary>
public sealed class StdoutWriter
{
    private readonly object _gate = new();
    private readonly TextWriter _out;

    public StdoutWriter(TextWriter output)
    {
        _out = output;
    }

    private void WriteLine(object message)
    {
        var json = JsonSerializer.Serialize(message, Protocol.JsonOptions);
        lock (_gate)
        {
            _out.Write(json);
            _out.Write('\n');
            _out.Flush();
        }
    }

    public void Result(string id, object? result) => WriteLine(new { id, ok = true, result });

    public void Error(string id, string error) => WriteLine(new { id, ok = false, error });

    public void Chunk(string id, object part) => WriteLine(new { id, @event = "chunk", part });

    public void End(string id) => WriteLine(new { id, @event = "end" });

    public void StreamError(string id, string message) => WriteLine(new { id, @event = "error", message });

    public void Log(string level, string message) => WriteLine(new { @event = "log", level, message });
}
