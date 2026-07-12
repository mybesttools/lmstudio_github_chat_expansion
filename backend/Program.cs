using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LmStudioBackend;
using LmStudioBackend.Tools;

var stdout = new StdoutWriter(Console.Out);
var logger = new Logger(stdout);

var config = new LmStudioConfig();
var configLock = new object();
LmStudioConfig GetConfig() { lock (configLock) return config; }

var client = new LmStudioClient(logger, GetConfig);
var orchestrator = new ChatOrchestrator(client, logger, GetConfig);

// Per-request-id cancellation, keyed by the IPC envelope id of the originating request
// (chatStream). A "cancel" message referencing that id cancels the in-flight call.
var inFlightCancellations = new ConcurrentDictionary<string, CancellationTokenSource>();

void ApplyConfigure(JsonObject? p)
{
    if (p == null) return;
    lock (configLock)
    {
        config = p.Deserialize<LmStudioConfig>(Protocol.JsonOptions) ?? config;
    }
    var logLevel = p["logLevel"]?.GetValue<string>();
    if (logLevel != null) logger.Level = Logger.Parse(logLevel);
}

async Task HandleRequestAsync(RequestEnvelope envelope)
{
    var id = envelope.Id;
    var paramsObj = envelope.Params as JsonObject;

    try
    {
        switch (envelope.Method)
        {
            case "configure":
                ApplyConfigure(paramsObj);
                stdout.Result(id, new { });
                return;

            case "checkConnection":
                stdout.Result(id, await client.CheckConnectionAsync());
                return;

            case "refreshModels":
                await orchestrator.RefreshModelsAsync();
                stdout.Result(id, orchestrator.GetAdvertisedModels());
                return;

            case "getAdvertisedModels":
                stdout.Result(id, orchestrator.GetAdvertisedModels());
                return;

            case "getTaskTypeModelConfigSuggestion":
                stdout.Result(id, await orchestrator.GetTaskTypeModelConfigSuggestionAsync());
                return;

            case "getMissingTaskTypeModels":
                stdout.Result(id, orchestrator.GetMissingTaskTypeModels());
                return;

            case "invokeTool":
            {
                var req = paramsObj.Deserialize<InvokeToolRequest>(Protocol.JsonOptions)
                          ?? throw new Exception("Missing invokeTool params");

                // Needs access to the live model catalog/task-profile config that only
                // ChatOrchestrator holds, so it bypasses the generic ToolInvoker dispatch.
                if (req.Name == SwitchModelTool.Name)
                {
                    stdout.Result(id, orchestrator.InvokeSwitchModelTool(req.Input));
                    return;
                }

                var cfg = GetConfig();
                req.Enabled = req.Name != TerminalTool.Name || cfg.EnableTerminalTool;
                var result = await ToolInvoker.InvokeAsync(req, cfg.TerminalToolTimeoutMs, CancellationToken.None);
                stdout.Result(id, result);
                return;
            }

            case "chatStream":
            {
                var req = paramsObj.Deserialize<ChatStreamRequest>(Protocol.JsonOptions)
                          ?? throw new Exception("Missing chatStream params");
                using var cts = new CancellationTokenSource();
                inFlightCancellations[id] = cts;
                try
                {
                    await foreach (var part in orchestrator.ChatStreamAsync(req, id, cts.Token))
                    {
                        if (part.Text != null)
                        {
                            stdout.Chunk(id, new { text = part.Text });
                        }
                        else if (part.ToolCall != null)
                        {
                            stdout.Chunk(id, new { toolCall = part.ToolCall });
                        }
                    }
                    stdout.End(id);
                }
                catch (OperationCanceledException)
                {
                    stdout.End(id);
                }
                finally
                {
                    inFlightCancellations.TryRemove(id, out _);
                }
                return;
            }

            case "cancel":
                if (inFlightCancellations.TryGetValue(id, out var targetCts))
                {
                    targetCts.Cancel();
                }
                return;

            default:
                stdout.Error(id, $"Unknown method: {envelope.Method}");
                return;
        }
    }
    catch (Exception e)
    {
        if (envelope.Method == "chatStream")
        {
            stdout.StreamError(id, e.Message);
        }
        else
        {
            stdout.Error(id, e.Message);
        }
    }
}

using var stdin = Console.OpenStandardInput();
using var reader = new StreamReader(stdin, Encoding.UTF8);

while (true)
{
    var line = await reader.ReadLineAsync();
    if (line == null) break;
    if (line.Length == 0) continue;

    RequestEnvelope? envelope;
    try
    {
        envelope = JsonSerializer.Deserialize<RequestEnvelope>(line, Protocol.JsonOptions);
    }
    catch (Exception e)
    {
        logger.Error($"Failed to parse request line: {e.Message}");
        continue;
    }
    if (envelope == null) continue;

    if (envelope.Method == "cancel")
    {
        // Handled synchronously and cheaply — no need to hop onto a background task.
        if (inFlightCancellations.TryGetValue(envelope.Id, out var cts)) cts.Cancel();
        continue;
    }

    // Every other request runs concurrently so a long-running chatStream doesn't block
    // subsequent requests (including its own eventual cancellation) from being read.
    _ = Task.Run(() => HandleRequestAsync(envelope));
}
