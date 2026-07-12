using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LmStudioBackend;

/// <summary>A streamed piece of a chat completion: either plain text or a fully-parsed tool call.</summary>
public sealed class StreamPart
{
    public string? Text { get; init; }
    public ToolCall? ToolCall { get; init; }

    public static StreamPart OfText(string text) => new() { Text = text };
    public static StreamPart OfToolCall(ToolCall call) => new() { ToolCall = call };
}

public sealed class ChatStreamOptions
{
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public double? TopP { get; init; }
    public string[]? Stop { get; init; }
    public List<ChatTool>? Tools { get; init; }
}

/// <summary>
/// Low-level HTTP/SSE client for LM Studio's OpenAI-compatible API. Direct port of src/lmstudio-client.ts.
///
/// Design principles (unchanged from the original):
///   - Stream text to the caller immediately, no unnecessary buffering.
///   - Buffer ONLY when we see the start of a <tool_call> tag.
///   - Once a tool call is yielded, suppress further text in that turn (models often
///     append filler like "I'm ready to help!" after tool calls).
///   - Strip <think>...</think> reasoning traces statefully across SSE chunks.
///   - Handle 3 tool-call formats: OpenAI delta.tool_calls, XML <tool_call>, and legacy <|channel|>...<|message|>.
/// </summary>
public sealed class LmStudioClient
{
    private static readonly Regex XmlToolCallRe = new(@"<tool_call>\s*([\s\S]*?)\s*</tool_call>", RegexOptions.Compiled);
    private static readonly Regex ChannelToolCallRe = new(
        @"<\|channel\|>.*?to=functions/([^\s<]+)\s*<\|constrain\|>json<\|message\|>([\s\S]*?\})(?=<\||$)",
        RegexOptions.Compiled);
    private static readonly Regex SimpleToolCallRe = new(@"<\|([a-zA-Z_][a-zA-Z0-9_]*)\|>(\{[\s\S]*?\})(?=<\||$)", RegexOptions.Compiled);
    private static readonly HashSet<string> SimpleToolCallSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "channel", "constrain", "message", "endoftext", "im_start", "im_end",
    };
    private static readonly Regex SpecialTokenRe = new(
        @"<\|(startofstream|endofstream|im_start|im_end|endoftext|end_of_turn|eot_id)\|>", RegexOptions.Compiled);
    private static readonly Regex ChannelStripRe = new(@"<\|channel\|>.*?(?=<\||$)", RegexOptions.Compiled);
    private static readonly Regex ConstrainStripRe = new(@"<\|constrain\|>.*?(?=<\||$)", RegexOptions.Compiled);
    private static readonly Regex MessageStripRe = new(@"<\|message\|>", RegexOptions.Compiled);
    private static readonly Regex ToolResponseTagRe = new(@"</?(tool_response|tool_call)>", RegexOptions.Compiled);

    private readonly HttpClient _http = new();
    private readonly Logger _logger;
    private readonly Func<LmStudioConfig> _getConfig;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();

    // Tracks in-flight /v1/chat/completions streams per model so a *different* concurrent
    // request's context-length reload can't unload the model out from under an active
    // generation. ensureModelLoadedOnce() waits on this before reloading.
    private readonly ConcurrentDictionary<string, int> _activeGenerationCount = new();
    private readonly ConcurrentDictionary<string, List<TaskCompletionSource>> _idleWaiters = new();
    private readonly object _generationGate = new();

    // Per-model in-flight ensureModelLoaded calls, so two requests targeting the same
    // not-yet-loaded model can't each race their own unload/load calls.
    private readonly ConcurrentDictionary<string, Task<bool>> _modelLoadInFlight = new();

    public LmStudioClient(Logger logger, Func<LmStudioConfig> getConfig)
    {
        _logger = logger;
        _getConfig = getConfig;
    }

    private void Log(string msg) => _logger.Verbose($"[Client] {msg}");
    private void Warn(string msg) => _logger.Warn($"[Client] {msg}");
    private void Err(string msg) => _logger.Error($"[Client] {msg}");

    private void MarkGenerationStart(string modelId)
    {
        lock (_generationGate)
        {
            _activeGenerationCount[modelId] = _activeGenerationCount.GetValueOrDefault(modelId) + 1;
        }
    }

    private void MarkGenerationEnd(string modelId)
    {
        List<TaskCompletionSource>? waiters = null;
        lock (_generationGate)
        {
            var remaining = _activeGenerationCount.GetValueOrDefault(modelId, 1) - 1;
            if (remaining > 0)
            {
                _activeGenerationCount[modelId] = remaining;
                return;
            }
            _activeGenerationCount.TryRemove(modelId, out _);
            if (_idleWaiters.TryRemove(modelId, out waiters)) { }
        }
        if (waiters != null)
        {
            foreach (var w in waiters) w.TrySetResult();
        }
    }

    /// <summary>Resolves once no generation is actively streaming against modelId.</summary>
    private Task WaitForIdleGenerationAsync(string modelId)
    {
        lock (_generationGate)
        {
            if (!_activeGenerationCount.ContainsKey(modelId)) return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var list = _idleWaiters.GetOrAdd(modelId, _ => new List<TaskCompletionSource>());
            list.Add(tcs);
            return tcs.Task;
        }
    }

    private static bool IsModelLoaded(LmStudioModel model) =>
        model.Loaded ?? (model.LoadedInstances?.Count > 0);

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var config = _getConfig();
        var req = new HttpRequestMessage(method, $"{config.ServerUrl}{path}");
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
        return req;
    }

    // ── Connection check ──────────────────────────────────────────────────

    public async Task<bool> CheckConnectionAsync()
    {
        var config = _getConfig();
        Log($"Checking connection to {config.ServerUrl}...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = BuildRequest(HttpMethod.Get, "/api/v1/models");
            using var resp = await _http.SendAsync(req, cts.Token);
            Log($"Connection check: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception e)
        {
            Err($"Connection check failed: {e.Message}");
            return false;
        }
    }

    // ── Model listing ─────────────────────────────────────────────────────

    public async Task<List<LmStudioModel>> GetModelsAsync()
    {
        var config = _getConfig();
        Log($"Fetching models from {config.ServerUrl}/api/v1/models...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(config.RequestTimeoutMs));
            using var req = BuildRequest(HttpMethod.Get, "/api/v1/models");
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                throw new Exception($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            var raw = await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cts.Token);
            var modelsNode = raw?["models"] as JsonArray;
            List<LmStudioModel> models;

            if (modelsNode != null)
            {
                var rawModels = modelsNode
                    .Deserialize<List<LmStudioRawModel>>(Protocol.JsonOptions) ?? new();
                models = rawModels
                    .Where(m => m.Type != "embedding")
                    .Select(m => new LmStudioModel
                    {
                        Id = m.Key,
                        Object = "model",
                        OwnedBy = m.Publisher ?? "unknown",
                        Type = m.Type,
                        Publisher = m.Publisher,
                        DisplayName = m.DisplayName,
                        Architecture = m.Architecture,
                        MaxContextLength = m.MaxContextLength,
                        SizeBytes = m.SizeBytes,
                        ParamsString = m.ParamsString,
                        Capabilities = m.Capabilities,
                        LoadedInstances = m.LoadedInstances,
                        Loaded = m.LoadedInstances is { Count: > 0 },
                    })
                    .ToList();
                Log($"Filtered {modelsNode.Count} -> {models.Count} LLM models");
            }
            else if (raw is JsonArray arr)
            {
                models = arr.Deserialize<List<LmStudioModel>>(Protocol.JsonOptions) ?? new();
            }
            else if (raw?["data"] is JsonArray dataArr)
            {
                models = dataArr.Deserialize<List<LmStudioModel>>(Protocol.JsonOptions) ?? new();
                foreach (var m in models)
                {
                    m.Loaded ??= m.LoadedInstances is { Count: > 0 };
                }
            }
            else
            {
                Log("Unexpected response shape");
                models = new();
            }

            Log($"Found {models.Count} models: {string.Join(", ", models.Select(m => m.Id))}");
            return models;
        }
        catch (Exception e)
        {
            Err($"Error fetching models: {e.Message}");
            return new();
        }
    }

    /// <summary>
    /// Ensure modelId is loaded with at least requiredContextLength tokens of context. If it's
    /// already loaded but with a smaller context window than required, it is unloaded and
    /// reloaded with a bigger one.
    /// </summary>
    public async Task<bool> EnsureModelLoadedAsync(string modelId, int? requiredContextLength = null)
    {
        if (_modelLoadInFlight.TryGetValue(modelId, out var existing))
        {
            Log($"A load/reload for {modelId} is already in progress; waiting for it before checking again");
            await existing;
        }

        var attempt = EnsureModelLoadedOnceAsync(modelId, requiredContextLength);
        _modelLoadInFlight[modelId] = attempt;
        try
        {
            return await attempt;
        }
        finally
        {
            _modelLoadInFlight.TryRemove(new KeyValuePair<string, Task<bool>>(modelId, attempt));
        }
    }

    private async Task<bool> EnsureModelLoadedOnceAsync(string modelId, int? requiredContextLength)
    {
        var serverReady = await CheckConnectionAsync();
        if (!serverReady)
        {
            Err($"Cannot use model {modelId} because the LM Studio API server is unavailable");
            return false;
        }

        var models = await GetModelsAsync();
        var selected = models.FirstOrDefault(m => m.Id == modelId);
        if (selected == null)
        {
            Err($"Selected model is not exposed by the LM Studio API: {modelId}");
            return false;
        }

        var architectureMax = selected.MaxContextLength;
        int? targetContext = requiredContextLength.HasValue
            ? (architectureMax.HasValue ? Math.Min(requiredContextLength.Value, architectureMax.Value) : requiredContextLength)
            : null;

        if (IsModelLoaded(selected))
        {
            var loadedContext = selected.LoadedInstances?.FirstOrDefault()?.Config?.ContextLength;
            var needsBiggerContext = targetContext.HasValue && loadedContext.HasValue && loadedContext.Value < targetContext.Value;

            if (!needsBiggerContext)
            {
                return true;
            }

            Log($"Model {modelId} is loaded with context_length={loadedContext}, but ~{targetContext} tokens are needed; reloading with a larger context");

            // Don't unload out from under another request actively streaming from this model.
            await WaitForIdleGenerationAsync(modelId);

            foreach (var instance in selected.LoadedInstances ?? new())
            {
                await UnloadInstanceAsync(instance.Id);
            }
        }
        else
        {
            Log($"Model {modelId} is available but not loaded; requesting load via API");
        }

        try
        {
            var config = _getConfig();
            var body = new JsonObject { ["model"] = modelId };
            if (targetContext.HasValue) body["context_length"] = targetContext.Value;

            var timeoutMs = Math.Max(config.RequestTimeoutMs, 10 * 60 * 1000);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            using var req = BuildRequest(HttpMethod.Post, "/api/v1/models/load");
            req.Content = JsonContent.Create<JsonNode>(body, options: Protocol.JsonOptions);
            using var resp = await _http.SendAsync(req, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                var errorText = await resp.Content.ReadAsStringAsync();
                Err($"API model load failed for {modelId}: {(int)resp.StatusCode} {resp.ReasonPhrase} {errorText}");
                return false;
            }

            var loadResult = await resp.Content.ReadFromJsonAsync<LoadModelResponse>(Protocol.JsonOptions, cts.Token);
            Log($"Model load response for {modelId}: status={loadResult?.Status} instance={loadResult?.InstanceId} context_length={loadResult?.LoadConfig?.ContextLength}");
        }
        catch (Exception e)
        {
            Err($"API model load request failed for {modelId}: {e.Message}");
            return false;
        }

        return await WaitForModelAvailabilityAsync(modelId, Math.Max(_getConfig().RequestTimeoutMs, 10 * 60 * 1000));
    }

    private async Task<bool> WaitForModelAvailabilityAsync(string modelId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var models = await GetModelsAsync();
            var model = models.FirstOrDefault(m => m.Id == modelId);
            if (model != null && IsModelLoaded(model))
            {
                return true;
            }
            await Task.Delay(1500);
        }

        var finalModels = await GetModelsAsync();
        var finalModel = finalModels.FirstOrDefault(m => m.Id == modelId);
        return finalModel != null && IsModelLoaded(finalModel);
    }

    private async Task UnloadInstanceAsync(string instanceId)
    {
        var config = _getConfig();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(config.RequestTimeoutMs));
            using var req = BuildRequest(HttpMethod.Post, "/api/v1/models/unload");
            req.Content = JsonContent.Create<JsonNode>(new JsonObject { ["instance_id"] = instanceId }, options: Protocol.JsonOptions);
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Warn($"Failed to unload instance {instanceId}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
                return;
            }
            Log($"Unloaded instance {instanceId}");
        }
        catch (Exception e)
        {
            Warn($"Unload request failed for instance {instanceId}: {e.Message}");
        }
    }

    // ── Streaming chat completion ─────────────────────────────────────────

    public void CancelRequest(string requestId)
    {
        if (_cancellations.TryRemove(requestId, out var cts))
        {
            cts.Cancel();
        }
    }

    public async IAsyncEnumerable<StreamPart> StreamChatCompletionWithToolsAsync(
        string modelId,
        List<ChatMessage> messages,
        ChatStreamOptions options,
        string requestId,
        [EnumeratorCancellation] CancellationToken externalToken = default)
    {
        var config = _getConfig();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _cancellations[requestId] = cts;

        var normalized = NormalizeOutgoingMessages(messages);

        var body = new JsonObject
        {
            ["model"] = modelId,
            ["messages"] = JsonSerializer.SerializeToNode(normalized, Protocol.JsonOptions),
            ["stream"] = true,
            ["temperature"] = options.Temperature ?? 0.7,
            ["max_tokens"] = options.MaxTokens ?? 32663,
            ["top_p"] = options.TopP ?? 1,
        };
        if (options.Stop is { Length: > 0 }) body["stop"] = JsonSerializer.SerializeToNode(options.Stop);

        if (!config.EnableThinking)
        {
            body["enable_thinking"] = false;
            Log("enable_thinking=false (user setting)");
        }

        if (config.ReasoningEffort != "default")
        {
            body["reasoning_effort"] = config.ReasoningEffort;
            Log($"reasoning_effort={config.ReasoningEffort} (user setting)");
        }
        if (config.ModelIdleTtl > 0)
        {
            body["ttl"] = config.ModelIdleTtl;
        }
        if (options.Tools is { Count: > 0 })
        {
            body["tools"] = JsonSerializer.SerializeToNode(options.Tools, Protocol.JsonOptions);
            body["tool_choice"] = "auto";
            Log($"Request includes {options.Tools.Count} tools");
        }

        Log($"POST {config.ServerUrl}/v1/chat/completions");
        MarkGenerationStart(modelId);
        HttpResponseMessage resp;
        try
        {
            using var req = BuildRequest(HttpMethod.Post, "/v1/chat/completions");
            req.Content = JsonContent.Create<JsonNode>(body, options: Protocol.JsonOptions);
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                throw new Exception($"LM Studio API error: {(int)resp.StatusCode} - {text}");
            }
        }
        finally
        {
            // MarkGenerationEnd happens after the stream fully drains below; if the request
            // itself failed to even start, end it now so we don't leak a phantom generation.
        }

        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            await foreach (var part in ProcessSseStreamAsync(stream, cts.Token))
            {
                yield return part;
            }
        }
        finally
        {
            resp.Dispose();
            _cancellations.TryRemove(requestId, out _);
            MarkGenerationEnd(modelId);
        }
    }

    /// <summary>Legacy convenience wrapper: yields raw text only (used for the task-profile advisor prompt).</summary>
    public async IAsyncEnumerable<string> StreamChatCompletionAsync(
        string modelId,
        List<ChatMessage> messages,
        ChatStreamOptions options,
        string requestId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var part in StreamChatCompletionWithToolsAsync(modelId, messages, options, requestId, ct))
        {
            if (part.Text != null) yield return part.Text;
        }
    }

    // ====================================================================
    //  PRIVATE — SSE stream processing
    // ====================================================================

    private async IAsyncEnumerable<StreamPart> ProcessSseStreamAsync(Stream body, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8);

        var insideThink = false;
        var holdBuffer = "";
        var toolCallYielded = false;
        var oaiToolCalls = new SortedDictionary<int, (string Id, string Name, string Args)>();
        var idx = 0;

        // Some reasoning models/servers (observed with Qwen3-family models on the streaming path)
        // misroute the final answer into delta.reasoning/delta.reasoning_content instead of
        // delta.content, leaving content empty for the whole turn even though generation
        // succeeded. Buffer it so we have a fallback if content never shows up.
        var reasoningBuffer = new StringBuilder();
        var anyContent = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var rawLine = await reader.ReadLineAsync(ct);
            if (rawLine == null) break;
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line == "data: [DONE]")
            {
                if (holdBuffer.Length > 0)
                {
                    foreach (var p in FlushHoldBuffer(holdBuffer, toolCallYielded)) yield return p;
                    holdBuffer = "";
                }
                foreach (var (i, tc) in oaiToolCalls)
                {
                    Log($"OAI tool call [{i}]: id=\"{tc.Id}\" name=\"{tc.Name}\" args=\"{Truncate(tc.Args, 200)}\"");
                    if (!string.IsNullOrEmpty(tc.Id) && !string.IsNullOrEmpty(tc.Name))
                    {
                        toolCallYielded = true;
                        yield return StreamPart.OfToolCall(new ToolCall { Id = tc.Id, Type = "function", Function = new ToolCallFunction { Name = tc.Name, Arguments = tc.Args } });
                    }
                }
                oaiToolCalls.Clear();
                continue;
            }

            if (!line.StartsWith("data: ")) continue;
            JsonNode? chunk;
            try { chunk = JsonNode.Parse(line[6..]); }
            catch { continue; }

            var choice = chunk?["choices"]?.AsArray().FirstOrDefault();
            if (choice == null) continue;

            var finishReason = choice["finish_reason"]?.GetValue<string>();
            if (finishReason != null)
            {
                Log($"finish_reason: {finishReason} (accumulated {oaiToolCalls.Count} OAI tool calls)");
            }

            // ── OpenAI-style delta.tool_calls ──────────────────────────
            var deltaToolCalls = choice["delta"]?["tool_calls"]?.AsArray();
            if (deltaToolCalls != null)
            {
                foreach (var tcNode in deltaToolCalls)
                {
                    if (tcNode == null) continue;
                    var index = tcNode["index"]?.GetValue<int>() ?? 0;
                    var cur = oaiToolCalls.TryGetValue(index, out var existing) ? existing : ("", "", "");
                    var id = tcNode["id"]?.GetValue<string>();
                    var fnName = tcNode["function"]?["name"]?.GetValue<string>();
                    var fnArgs = tcNode["function"]?["arguments"]?.GetValue<string>();
                    oaiToolCalls[index] = (
                        !string.IsNullOrEmpty(id) ? id : cur.Item1,
                        !string.IsNullOrEmpty(fnName) ? fnName : cur.Item2,
                        cur.Item3 + (fnArgs ?? "")
                    );
                }
            }

            // ── Reasoning content (fallback only — see reasoningBuffer comment above) ──
            var rawReasoning = choice["delta"]?["reasoning_content"]?.GetValue<string>()
                ?? choice["delta"]?["reasoning"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(rawReasoning)) reasoningBuffer.Append(rawReasoning);

            // ── Text content ──────────────────────────────────────────
            var rawContent = choice["delta"]?["content"]?.GetValue<string>();
            if (string.IsNullOrEmpty(rawContent)) continue;
            anyContent = true;

            var text = FilterText(rawContent, insideThink, out var stillInsideThink);
            insideThink = stillInsideThink;
            if (text.Length == 0) continue;

            holdBuffer += text;

            var foundToolCall = false;

            if (holdBuffer.Contains("</tool_call>"))
            {
                var (calls, remaining) = ParseXmlToolCalls(holdBuffer, ref idx);
                if (calls.Count > 0)
                {
                    foreach (var tc in calls) { toolCallYielded = true; yield return StreamPart.OfToolCall(tc); }
                    holdBuffer = remaining;
                    foundToolCall = true;
                }
            }

            if (holdBuffer.Contains("<|channel|>") && holdBuffer.Contains("<|message|>") && holdBuffer.Contains('}'))
            {
                var (calls, remaining) = ParseLegacyToolCalls(holdBuffer, ref idx);
                if (calls.Count > 0)
                {
                    foreach (var tc in calls) { toolCallYielded = true; yield return StreamPart.OfToolCall(tc); }
                    holdBuffer = remaining;
                    foundToolCall = true;
                }
            }

            if (foundToolCall) continue;

            if (holdBuffer.Contains("<tool_call") || holdBuffer.Contains("<|channel|>"))
            {
                continue;
            }

            if (!toolCallYielded)
            {
                yield return StreamPart.OfText(holdBuffer);
            }
            holdBuffer = "";
        }

        if (holdBuffer.Length > 0)
        {
            foreach (var p in FlushHoldBuffer(holdBuffer, toolCallYielded)) yield return p;
        }
        foreach (var (_, tc) in oaiToolCalls)
        {
            if (!string.IsNullOrEmpty(tc.Id) && !string.IsNullOrEmpty(tc.Name))
            {
                toolCallYielded = true;
                yield return StreamPart.OfToolCall(new ToolCall { Id = tc.Id, Type = "function", Function = new ToolCallFunction { Name = tc.Name, Arguments = tc.Args } });
            }
        }

        // Fallback: some reasoning-model servers (seen with Qwen3-family models, e.g.
        // https://github.com/vllm-project/vllm/issues/40816) route the entire answer through
        // delta.reasoning/delta.reasoning_content on the streaming path and leave delta.content
        // empty for the whole turn, even though generation succeeded. If that happened, recover
        // the answer from the reasoning buffer rather than silently returning nothing.
        if (!anyContent && !toolCallYielded && reasoningBuffer.Length > 0)
        {
            var reasoningIdx = 0;
            var filtered = FilterText(reasoningBuffer.ToString(), false, out _);
            var (calls, remaining) = ParseXmlToolCalls(filtered, ref reasoningIdx);
            foreach (var tc in calls) { toolCallYielded = true; yield return StreamPart.OfToolCall(tc); }
            if (calls.Count == 0)
            {
                var cleaned = remaining.Trim();
                if (cleaned.Length > 0)
                {
                    Warn($"delta.content was empty for the whole turn; recovered {cleaned.Length} chars from the reasoning field instead (server-side reasoning/content routing bug)");
                    yield return StreamPart.OfText(cleaned);
                }
            }
        }
    }

    /// <summary>Flush the hold buffer: extract tool calls, then emit remaining text only if no tool call was ever yielded.</summary>
    private IEnumerable<StreamPart> FlushHoldBuffer(string buf, bool alreadyYielded)
    {
        var idx = 0;
        var (xmlCalls, xmlRemaining) = ParseXmlToolCalls(buf, ref idx);
        var (legacyCalls, legacyRemaining) = ParseLegacyToolCalls(xmlRemaining, ref idx);
        var allCalls = xmlCalls.Concat(legacyCalls).ToList();
        foreach (var tc in allCalls) yield return StreamPart.OfToolCall(tc);
        if (allCalls.Count == 0 && !alreadyYielded)
        {
            var cleaned = legacyRemaining.Trim();
            if (cleaned.Length > 0) yield return StreamPart.OfText(cleaned);
        }
    }

    // ── Text filtering ────────────────────────────────────────────────────

    /// <summary>Strip &lt;think&gt;...&lt;/think&gt; and special tokens from a chunk.</summary>
    private static string FilterText(string raw, bool insideThink, out bool stillInside)
    {
        var sb = new StringBuilder();
        var i = 0;
        var inside = insideThink;
        while (i < raw.Length)
        {
            if (inside)
            {
                var close = raw.IndexOf("</think>", i, StringComparison.Ordinal);
                if (close == -1) break;
                i = close + "</think>".Length;
                if (i < raw.Length && raw[i] == '\n') i++;
                inside = false;
            }
            else
            {
                var open = raw.IndexOf("<think>", i, StringComparison.Ordinal);
                if (open == -1) { sb.Append(raw, i, raw.Length - i); break; }
                sb.Append(raw, i, open - i);
                i = open + "<think>".Length;
                inside = true;
            }
        }
        stillInside = inside;
        return SpecialTokenRe.Replace(sb.ToString(), "");
    }

    // ── Tool-call parsers ─────────────────────────────────────────────────

    /// <summary>Parse &lt;tool_call&gt;{...}&lt;/tool_call&gt; blocks.</summary>
    private (List<ToolCall> Calls, string Remaining) ParseXmlToolCalls(string text, ref int idx)
    {
        var calls = new List<ToolCall>();
        var remaining = text;
        foreach (Match m in XmlToolCallRe.Matches(text))
        {
            try
            {
                var parsed = JsonNode.Parse(m.Groups[1].Value.Trim())?.AsObject();
                var name = parsed?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(name))
                {
                    var argsNode = parsed?["arguments"];
                    var argsStr = argsNode is JsonValue v && v.TryGetValue<string>(out var s) ? s : (argsNode?.ToJsonString() ?? "{}");
                    calls.Add(new ToolCall { Id = $"call_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{idx++}", Type = "function", Function = new ToolCallFunction { Name = name, Arguments = argsStr } });
                    remaining = remaining.Replace(m.Value, "");
                    Log($"Parsed XML tool call: {name}");
                }
            }
            catch (Exception e) { Log($"XML tool-call parse error: {e.Message}"); }
        }
        return (calls, remaining);
    }

    /// <summary>Parse legacy &lt;|channel|&gt;...&lt;|message|&gt;{...} and &lt;|fn_name|&gt;{...} formats.</summary>
    private (List<ToolCall> Calls, string Remaining) ParseLegacyToolCalls(string text, ref int idx)
    {
        var calls = new List<ToolCall>();
        var remaining = text;

        foreach (Match m in ChannelToolCallRe.Matches(text))
        {
            try
            {
                JsonNode.Parse(m.Groups[2].Value);
                calls.Add(new ToolCall { Id = $"call_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{idx++}", Type = "function", Function = new ToolCallFunction { Name = m.Groups[1].Value, Arguments = m.Groups[2].Value } });
                remaining = remaining.Replace(m.Value, "");
                Log($"Parsed legacy tool call: {m.Groups[1].Value}");
            }
            catch { /* skip */ }
        }

        foreach (Match m in SimpleToolCallRe.Matches(remaining))
        {
            if (SimpleToolCallSkip.Contains(m.Groups[1].Value)) continue;
            try
            {
                JsonNode.Parse(m.Groups[2].Value);
                calls.Add(new ToolCall { Id = $"call_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{idx++}", Type = "function", Function = new ToolCallFunction { Name = m.Groups[1].Value, Arguments = m.Groups[2].Value } });
                remaining = remaining.Replace(m.Value, "");
                Log($"Parsed simple tool call: {m.Groups[1].Value}");
            }
            catch { /* skip */ }
        }

        remaining = ChannelStripRe.Replace(remaining, "");
        remaining = ConstrainStripRe.Replace(remaining, "");
        remaining = MessageStripRe.Replace(remaining, "");
        remaining = remaining.Trim();

        return (calls, remaining);
    }

    // ── Outgoing message normalization ────────────────────────────────────

    private List<ChatMessage> NormalizeOutgoingMessages(List<ChatMessage> messages)
    {
        var result = new List<ChatMessage>();
        foreach (var m in messages)
        {
            var sanitized = SanitizeOutgoingContent(m.Content);
            var empty = IsContentEmpty(sanitized);

            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                result.Add(new ChatMessage { Role = m.Role, Content = empty ? null : sanitized, ToolCallId = m.ToolCallId, ToolCalls = m.ToolCalls });
                continue;
            }

            if (empty) continue;

            result.Add(new ChatMessage { Role = m.Role, Content = sanitized, ToolCallId = m.ToolCallId, ToolCalls = m.ToolCalls });
        }
        return result;
    }

    public static bool IsContentEmpty(JsonNode? content)
    {
        if (content == null) return true;
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s.Trim().Length == 0;
        if (content is JsonArray arr)
        {
            if (arr.Count == 0) return true;
            return arr.All(part => part?["type"]?.GetValue<string>() == "text" && (part["text"]?.GetValue<string>() ?? "").Trim().Length == 0);
        }
        return false;
    }

    private static JsonNode? SanitizeOutgoingContent(JsonNode? content)
    {
        if (content == null) return null;

        if (content is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var partNode in arr)
            {
                if (partNode?["type"]?.GetValue<string>() == "text")
                {
                    var cleaned = CleanText(partNode["text"]?.GetValue<string>() ?? "");
                    if (cleaned.Length > 0)
                    {
                        result.Add(new JsonObject { ["type"] = "text", ["text"] = cleaned });
                    }
                }
                else if (partNode != null)
                {
                    result.Add(partNode.DeepClone());
                }
            }
            return result;
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var str))
        {
            return CleanText(str);
        }

        return content;
    }

    private static string CleanText(string text) =>
        ToolResponseTagRe.Replace(SpecialTokenRe.Replace(text, ""), "").Trim();

    private static string Truncate(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars];
}
