using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LmStudioBackend.Tools;

namespace LmStudioBackend;

/// <summary>
/// Direct port of the non-VS-Code-bound half of src/lmstudio-provider.ts: model advertising,
/// task-profile scoring/advisories, tool budgeting, and the chat-response orchestration.
/// The VS Code shell still owns converting vscode.LanguageModelChatRequestMessage[] to/from the
/// wire ChatMessage[] format used here, since that conversion touches vscode-only types.
/// </summary>
public sealed class ChatOrchestrator
{
    private static readonly string[] TaskProfileKeys = { "quick", "coding", "planning", "toolUse", "vision", "review" };

    private static readonly Dictionary<string, string> TaskProfileLabels = new()
    {
        ["quick"] = "Quick Tasks",
        ["coding"] = "Coding",
        ["planning"] = "Planning",
        ["toolUse"] = "Tool Use",
        ["vision"] = "Vision",
        ["review"] = "Review",
    };

    private static readonly Dictionary<string, string> TaskProfileDescriptions = new()
    {
        ["quick"] = "Quick edits, short answers, and low-complexity requests.",
        ["coding"] = "General coding tasks.",
        ["planning"] = "Long-context reasoning, architecture work, and synthesis tasks.",
        ["toolUse"] = "Requests that expose many tools or rely heavily on tool calling.",
        ["vision"] = "Image-input or vision tasks.",
        ["review"] = "Code review, summarization, or final-pass validation tasks.",
    };

    // Output-token reserve used when *sizing a context window*, capped regardless of how high
    // the user's configured maxOutputTokens is — reserving the full configured value on every
    // reload forces a far larger context window than the request needs, which can exceed
    // available VRAM and crash the inference backend mid-generation.
    private const int ContextReserveCap = 4096;

    // Virtual model id for "LM Studio Auto": not backed by one fixed model. ChatStreamAsync
    // resolves the real backing model per conversation (see ResolveAutoModelIdAsync) instead of
    // reading AdvertisedModel.LmstudioModelId directly.
    private const string AutoModelId = "lmstudio-auto";

    // Marks the start of the routing note re-injected into chat history by "LM Studio Auto" so a
    // later turn in the same conversation can find its own prior pick (FindLatestRoutedModelId)
    // and stick with it instead of re-classifying and potentially reload-thrashing every message.
    private static readonly Regex AutoRoutedRe = new(@"^\[LM Studio Auto → (\S+)\]", RegexOptions.Compiled);

    // Marks a mbt_lmstudio_switch_model tool result (see InvokeSwitchModelTool), letting the
    // model itself hand the rest of an Auto-routed conversation off to a different profile/model.
    private static readonly Regex SwitchModelRe = new(@"\[LM Studio switched to (\S+)\]", RegexOptions.Compiled);

    private readonly LmStudioClient _client;
    private readonly Logger _logger;
    private readonly Func<LmStudioConfig> _getConfig;

    private List<LmStudioModel> _availableModels = new();
    private Dictionary<string, AdvertisedModel> _advertisedById = new();

    public ChatOrchestrator(LmStudioClient client, Logger logger, Func<LmStudioConfig> getConfig)
    {
        _client = client;
        _logger = logger;
        _getConfig = getConfig;
    }

    private void Log(string msg) => _logger.Verbose($"[Provider] {msg}");
    private void Warn(string msg) => _logger.Warn($"[Provider] {msg}");

    public List<LmStudioModel> GetAvailableModels() => _availableModels;

    // ──────────────────────────────────────────────────────────────────────
    // Model discovery / advertising
    // ──────────────────────────────────────────────────────────────────────

    public async Task RefreshModelsAsync()
    {
        Log("Refreshing models...");
        if (!await _client.CheckConnectionAsync())
        {
            Warn($"Cannot reach the LM Studio server at {_getConfig().ServerUrl}; skipping model refresh.");
            _availableModels = new();
            return;
        }

        var liveModels = await _client.GetModelsAsync();

        _availableModels = liveModels
            .OrderByDescending(m => m.Loaded == true ? 1 : 0)
            .ThenBy(GetDisplayName, StringComparer.Ordinal)
            .ToList();

        Log(_availableModels.Count == 0
            ? "No models available from LM Studio"
            : $"Found {_availableModels.Count} model(s): {string.Join(", ", _availableModels.Select(m => m.Id))}");
    }

    private LmStudioModel? GetModelById(string modelId) => _availableModels.FirstOrDefault(m => m.Id == modelId);

    private List<(string Key, string ModelId)> GetTaskProfiles()
    {
        var config = _getConfig();
        return TaskProfileKeys
            .Select(key => config.TaskTypeModels.TryGetValue(key, out var id) && !string.IsNullOrWhiteSpace(id) ? (key, id.Trim()) : ((string, string)?)null)
            .Where(entry => entry.HasValue)
            .Select(entry => entry!.Value)
            .ToList();
    }

    private int GetEffectiveContextLength(LmStudioModel? model)
    {
        if (model == null) return 0;
        var loadedContext = model.LoadedInstances?.FirstOrDefault()?.Config?.ContextLength;
        if (loadedContext is > 0) return loadedContext.Value;
        return model.MaxContextLength ?? 0;
    }

    private AdvertisedModel ToAdvertisedModel(LmStudioModel model, int maxInputTokens, int maxOutputTokens, bool enableToolCalling, string? id = null, string? name = null, string? taskProfileKey = null)
    {
        return new AdvertisedModel
        {
            Id = id ?? model.Id,
            Name = name ?? GetDisplayName(model),
            MaxInputTokens = GetEffectiveContextLength(model) is var ctx && ctx > 0 ? ctx : maxInputTokens,
            MaxOutputTokens = maxOutputTokens,
            LmstudioModelId = model.Id,
            IsLoaded = model.Loaded == true,
            ToolCalling = enableToolCalling,
            ImageInput = model.Capabilities?.Vision ?? false,
            TaskProfileKey = taskProfileKey,
        };
    }

    private AdvertisedModel BuildAutoModel(LmStudioConfig config)
    {
        var maxCtx = _availableModels.Count > 0 ? _availableModels.Max(GetEffectiveContextLength) : 0;
        return new AdvertisedModel
        {
            Id = AutoModelId,
            Name = "LM Studio Auto",
            MaxInputTokens = maxCtx > 0 ? maxCtx : config.MaxInputTokens,
            MaxOutputTokens = config.MaxOutputTokens,
            LmstudioModelId = AutoModelId,
            IsLoaded = _availableModels.Any(m => m.Loaded == true),
            ToolCalling = config.EnableToolCalling,
            ImageInput = _availableModels.Any(m => m.Capabilities?.Vision == true),
            TaskProfileKey = null,
        };
    }

    public List<AdvertisedModel> GetAdvertisedModels()
    {
        var config = _getConfig();
        var taskProfiles = GetTaskProfiles();
        var profileBackingIds = taskProfiles.Select(p => p.ModelId).ToHashSet();

        var directModels = _availableModels
            .Where(m => !profileBackingIds.Contains(m.Id))
            .Select(m => ToAdvertisedModel(m, config.MaxInputTokens, config.MaxOutputTokens, config.EnableToolCalling))
            .ToList();

        var profileModels = new List<AdvertisedModel>();
        foreach (var (key, modelId) in taskProfiles)
        {
            var backing = GetModelById(modelId);
            if (backing == null)
            {
                if (_availableModels.Count > 0)
                {
                    Warn($"Task profile {key} points to an unknown model: {modelId}");
                }
                continue;
            }
            profileModels.Add(ToAdvertisedModel(
                backing, config.MaxInputTokens, config.MaxOutputTokens, config.EnableToolCalling,
                id: $"task-profile:{key}", name: $"{TaskProfileLabels[key]} ({GetDisplayName(backing)})", taskProfileKey: key));
        }

        var result = profileModels.Concat(directModels).ToList();
        if (config.EnableAutoModel && _availableModels.Count > 0)
        {
            result.Insert(0, BuildAutoModel(config));
        }
        _advertisedById = result.ToDictionary(m => m.Id);
        return result;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Token / image estimation over wire ChatMessage[]
    // ──────────────────────────────────────────────────────────────────────

    private static string ExtractMessageText(ChatMessage m)
    {
        if (m.Content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (m.Content is JsonArray arr)
        {
            return string.Concat(arr
                .Where(p => p?["type"]?.GetValue<string>() == "text")
                .Select(p => p!["text"]?.GetValue<string>() ?? ""));
        }
        return "";
    }

    private static int CountImages(ChatMessage m)
    {
        if (m.Content is not JsonArray arr) return 0;
        return arr.Count(p => p?["type"]?.GetValue<string>() == "image_url");
    }

    private static int EstimateRequestTokens(List<ChatMessage> messages)
    {
        var chars = messages.Sum(m => ExtractMessageText(m).Length);
        var imageTokens = messages.Sum(CountImages) * 4000;
        return (int)Math.Ceiling(chars / 4.0) + imageTokens;
    }

    private static int CountImages(List<ChatMessage> messages) => messages.Sum(CountImages);

    private static int EstimateToolTokens(ToolSchema tool)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema }, Protocol.JsonOptions);
        return (int)Math.Ceiling(json.Length / 4.0);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Task-profile scoring heuristic
    // ──────────────────────────────────────────────────────────────────────

    private static readonly Regex SizeRe = new(@"(\d+(?:\.\d+)?)\s*([BM])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static double? ParseModelSizeBillions(LmStudioModel model)
    {
        var raw = model.ParamsString?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        var match = SizeRe.Match(raw);
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value, out var value)) return null;
        return match.Groups[2].Value.ToUpperInvariant() == "M" ? value / 1000 : value;
    }

    private static double? GetModelSizeBillions(LmStudioModel model)
    {
        var parsed = ParseModelSizeBillions(model);
        if (parsed != null) return parsed;
        if (model.SizeBytes is > 0) return model.SizeBytes.Value / 1_000_000_000.0;
        return null;
    }

    private static string GetModelSearchText(LmStudioModel model) => string.Join(' ', new[]
    {
        model.Id, model.DisplayName, model.Architecture, model.Publisher, model.ParamsString,
    }.Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();

    private static double KeywordBonus(string text, params (string Pattern, double Bonus)[] bonuses) =>
        bonuses.Where(b => Regex.IsMatch(text, b.Pattern)).Sum(b => b.Bonus);

    private static double ScoreModelForProfile(string profileKey, LmStudioModel model)
    {
        var text = GetModelSearchText(model);
        var sizeB = GetModelSizeBillions(model);
        var contextLength = model.MaxContextLength ?? 0;
        double score = 0;

        if (model.Loaded == true) score += 1000;
        if (model.Capabilities?.Vision == true) score += profileKey == "vision" ? 6000 : 250;
        if (model.Capabilities?.TrainedForToolUse == true) score += profileKey == "toolUse" ? 4000 : 250;
        if (profileKey == "vision" && model.Capabilities?.Vision != true) score -= 500;

        switch (profileKey)
        {
            case "quick":
                if (sizeB != null) score += Math.Max(0, 600 - sizeB.Value * 35);
                score += KeywordBonus(text, (@"\bmini\b", 180), (@"\bsmall\b", 160), (@"\bfast\b", 140), (@"\btiny\b", 140), (@"\blite\b", 120));
                break;
            case "coding":
                if (sizeB != null) score += 300 - Math.Abs(sizeB.Value - 14) * 18;
                score += contextLength / 5000.0;
                score += KeywordBonus(text, (@"\bcode\b", 260), (@"\bcoder\b", 260), (@"\bdev\b", 120), (@"\bdeepseek\b", 120), (@"\bqwen\b", 120), (@"\bstarcoder\b", 180), (@"\bcodestral\b", 180), (@"\bphi\b", 120));
                break;
            case "planning":
                if (sizeB != null) score += sizeB.Value * 45;
                score += contextLength / 3000.0;
                score += KeywordBonus(text, (@"\breason\b", 180), (@"\bplanning\b", 180), (@"\bthink\b", 120), (@"\binstruct\b", 100), (@"\blarge\b", 120), (@"\b70b\b", 140));
                break;
            case "toolUse":
                if (sizeB != null) score += 240 - Math.Abs(sizeB.Value - 20) * 10;
                score += contextLength / 5000.0;
                score += KeywordBonus(text, (@"\btool\b", 260), (@"\bagent\b", 220), (@"\bfunction\b", 220), (@"\btools\b", 220), (@"\bcall\b", 120));
                break;
            case "vision":
                if (sizeB != null) score += sizeB.Value * 20;
                score += contextLength / 5000.0;
                score += KeywordBonus(text, (@"\bvision\b", 500), (@"\bvl\b", 120), (@"\bmultimodal\b", 160), (@"\bimage\b", 120));
                break;
            case "review":
                if (sizeB != null) score += sizeB.Value * 40;
                score += contextLength / 4000.0;
                score += KeywordBonus(text, (@"\breview\b", 220), (@"\bcode\b", 180), (@"\bcoder\b", 180), (@"\binstruct\b", 100), (@"\breason\b", 120), (@"\bqa\b", 80));
                break;
        }

        return score;
    }

    private LmStudioModel? SelectSuggestedModel(string profileKey)
    {
        if (_availableModels.Count == 0) return null;
        return _availableModels
            .OrderByDescending(m => ScoreModelForProfile(profileKey, m))
            .ThenBy(GetDisplayName, StringComparer.Ordinal)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string DescribeProfileFit(string profileKey, LmStudioModel model) => profileKey switch
    {
        "quick" => model.Loaded == true ? "small and already loaded" : "smallest fit from the connected API",
        "coding" => "code-oriented fit for day-to-day coding tasks",
        "planning" => "larger-context fit for planning and synthesis",
        "toolUse" => model.Capabilities?.TrainedForToolUse == true ? "tool-use trained model" : "best available tool-friendly model",
        "vision" => model.Capabilities?.Vision == true ? "vision-capable model" : "best available multimodal fallback",
        "review" => "good fit for review and final-pass validation",
        _ => "",
    };

    private string? GetProfileRecommendation(string profileKey)
    {
        var profile = GetTaskProfiles().FirstOrDefault(p => p.Key == profileKey);
        if (profile == default) return null;
        var model = GetModelById(profile.ModelId);
        return model == null ? null : $"{TaskProfileLabels[profileKey]} ({GetDisplayName(model)})";
    }

    private LmStudioModel? SelectAdvisorModel()
    {
        var loaded = _availableModels.Where(m => m.Loaded == true).ToList();
        if (loaded.Count == 0) return null;
        return loaded
            .OrderByDescending(m => GetModelSizeBillions(m) ?? 0)
            .ThenByDescending(m => m.MaxContextLength ?? 0)
            .First();
    }

    private static string DescribeModelForAdvisor(LmStudioModel model)
    {
        var sizeB = GetModelSizeBillions(model);
        var parts = new List<string>
        {
            $"id: {model.Id}",
        };
        if (sizeB != null) parts.Add($"~{sizeB.Value:F1}B params");
        if (model.MaxContextLength is > 0) parts.Add($"{model.MaxContextLength} ctx");
        if (model.Capabilities?.Vision == true) parts.Add("vision");
        if (model.Capabilities?.TrainedForToolUse == true) parts.Add("tool-use trained");
        parts.Add(model.Loaded == true ? "loaded" : "not loaded");
        return $"- {string.Join(", ", parts)}";
    }

    private async Task<Dictionary<string, string>?> RequestAdvisorPicksAsync(LmStudioModel advisor, string[] keys)
    {
        var catalog = string.Join('\n', _availableModels.Select(DescribeModelForAdvisor));
        var profiles = string.Join('\n', keys.Select(k => $"- \"{k}\": {TaskProfileDescriptions[k]}"));

        var prompt = string.Join('\n',
            "You are configuring per-task model routing for a coding assistant connected to a local LM Studio server.",
            "Available models:",
            catalog,
            "",
            "Task profiles to fill in:",
            profiles,
            "",
            "Pick the best-fitting model id for each profile from the list above. Reusing the same model id for " +
            "multiple profiles is fine if it is genuinely the best (or only) fit.",
            "Respond with ONLY a JSON object mapping each profile key to a model id, e.g. " +
            "{\"quick\":\"...\",\"coding\":\"...\",\"planning\":\"...\",\"toolUse\":\"...\",\"vision\":\"...\",\"review\":\"...\"}. " +
            "No prose, no markdown fences.");

        var requestId = $"task-profile-advisor-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var text = "";
        var messages = new List<ChatMessage> { new() { Role = "user", Content = JsonValue.Create(prompt) } };
        await foreach (var chunk in _client.StreamChatCompletionAsync(advisor.Id, messages, new ChatStreamOptions { Temperature = 0.2, MaxTokens = 400 }, requestId, cts.Token))
        {
            text += chunk;
        }

        var match = Regex.Match(text, @"\{[\s\S]*\}");
        if (!match.Success)
        {
            Warn($"Advisor response had no JSON object: {(text.Length > 200 ? text[..200] : text)}");
            return null;
        }

        JsonObject? parsed;
        try { parsed = JsonNode.Parse(match.Value)?.AsObject(); }
        catch (Exception e) { Warn($"Advisor response JSON parse failed: {e.Message}"); return null; }
        if (parsed == null) return null;

        var picks = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            var value = parsed[key]?.GetValue<string>()?.Trim();
            if (!string.IsNullOrEmpty(value) && GetModelById(value) != null)
            {
                picks[key] = value;
            }
            else if (parsed[key] != null)
            {
                Warn($"Advisor picked an unknown model for {key}: {parsed[key]}");
            }
        }

        return picks.Count > 0 ? picks : null;
    }

    private Dictionary<string, string> GetConfiguredTaskTypeModels()
    {
        var config = _getConfig();
        return TaskProfileKeys
            .Where(k => config.TaskTypeModels.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
            .ToDictionary(k => k, k => config.TaskTypeModels[k].Trim());
    }

    private bool IsModelMissing(string modelId) => !string.IsNullOrEmpty(modelId) && GetModelById(modelId) == null;

    private TaskTypeModelConfigSuggestion? BuildSuggestionFromPicks(Dictionary<string, string> picks, LmStudioModel? advisorModel)
    {
        var current = GetConfiguredTaskTypeModels();
        var suggested = new Dictionary<string, string>();
        var changes = new List<TaskProfileChange>();
        var details = new List<string>();

        foreach (var key in TaskProfileKeys)
        {
            if (!picks.TryGetValue(key, out var suggestedModelId)) continue;
            var suggestedModel = GetModelById(suggestedModelId);
            if (suggestedModel == null) continue;

            var currentModelId = current.GetValueOrDefault(key, "");
            if (currentModelId == suggestedModelId) continue;

            suggested[key] = suggestedModelId;
            var isMissing = IsModelMissing(currentModelId);
            var currentModelName = string.IsNullOrEmpty(currentModelId)
                ? "unset"
                : (GetModelById(currentModelId) is { } cm ? GetDisplayName(cm) : FormatModelName(currentModelId));

            var suggestedModelName = GetDisplayName(suggestedModel);
            var reason = isMissing
                ? "previously configured model is no longer available on the server"
                : (advisorModel != null ? $"recommended by {GetDisplayName(advisorModel)}" : DescribeProfileFit(key, suggestedModel));

            changes.Add(new TaskProfileChange
            {
                Key = key, CurrentModelId = currentModelId, CurrentModelName = currentModelName,
                SuggestedModelId = suggestedModelId, SuggestedModelName = suggestedModelName, Reason = reason,
                IsMissing = isMissing,
            });
            details.Add($"- {TaskProfileLabels[key]}: {currentModelName} -> {suggestedModelName} ({reason})");
        }

        if (changes.Count == 0) return null;

        var updated = new Dictionary<string, string>(current);
        foreach (var (k, v) in suggested) updated[k] = v;

        var missingCount = changes.Count(c => c.IsMissing);
        var summary = missingCount > 0
            ? $"{missingCount} configured task profile model{(missingCount == 1 ? "" : "s")} {(missingCount == 1 ? "is" : "are")} no longer available on the server" +
              (changes.Count > missingCount ? $"; suggested replacements for {changes.Count} profile{(changes.Count == 1 ? "" : "s")} total." : ".")
            : $"Suggested model config differs for {changes.Count} task profile{(changes.Count == 1 ? "" : "s")}" +
              (advisorModel != null ? $" (advised by {GetDisplayName(advisorModel)})." : ".");

        return new TaskTypeModelConfigSuggestion
        {
            Summary = summary,
            Details = details,
            UpdatedTaskTypeModels = updated,
            Changes = changes,
            HasMissingModels = missingCount > 0,
        };
    }

    /// <summary>Lightweight check (no advisor call, no scoring) for whether any configured task-profile model id no longer resolves against the connected server's model catalog.</summary>
    public List<MissingTaskTypeModel> GetMissingTaskTypeModels()
    {
        if (_availableModels.Count == 0) return new();

        return GetConfiguredTaskTypeModels()
            .Where(kv => IsModelMissing(kv.Value))
            .Select(kv => new MissingTaskTypeModel { Key = kv.Key, Label = TaskProfileLabels[kv.Key], ModelId = kv.Value })
            .ToList();
    }

    public async Task<TaskTypeModelConfigSuggestion?> GetTaskTypeModelConfigSuggestionAsync()
    {
        if (_availableModels.Count == 0) return null;
        if (!await _client.CheckConnectionAsync())
        {
            Warn($"Cannot reach the LM Studio server at {_getConfig().ServerUrl}; skipping task-profile config suggestion.");
            return null;
        }

        var heuristicPicks = new Dictionary<string, string>();
        foreach (var key in TaskProfileKeys)
        {
            var model = SelectSuggestedModel(key);
            if (model != null) heuristicPicks[key] = model.Id;
        }

        Dictionary<string, string>? advisorPicks = null;
        LmStudioModel? advisorModel = null;
        var advisor = SelectAdvisorModel();
        if (advisor != null)
        {
            try
            {
                var advisorResult = await RequestAdvisorPicksAsync(advisor, TaskProfileKeys);
                if (advisorResult != null) { advisorPicks = advisorResult; advisorModel = advisor; }
            }
            catch (Exception e)
            {
                Warn($"Advisor-based task profile recommendation failed, falling back to heuristic: {e.Message}");
            }
        }

        var picks = new Dictionary<string, string>(heuristicPicks);
        if (advisorPicks != null) foreach (var (k, v) in advisorPicks) picks[k] = v;

        return BuildSuggestionFromPicks(picks, advisorModel);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Model advisory (per-request warnings/recommendations)
    // ──────────────────────────────────────────────────────────────────────

    private static bool ConversationAlreadyAdvised(List<ChatMessage> messages) =>
        messages.Any(m => m.Role == "assistant" && ExtractMessageText(m).StartsWith("[LM Studio advisor]", StringComparison.Ordinal));

    private string? BuildModelAdvisory(AdvertisedModel selected, LmStudioModel? backing, List<ChatMessage> messages, ChatModelOptions? modelOptions, List<ChatTool>? selectedTools)
    {
        var config = _getConfig();
        if (!config.EnableModelAdvisories || backing == null) return null;
        if (ConversationAlreadyAdvised(messages)) return null;

        var approxTokens = EstimateRequestTokens(messages);
        var imageCount = CountImages(messages);
        var toolCount = selectedTools?.Count ?? 0;
        var messageCount = messages.Count;
        var contextLimit = GetEffectiveContextLength(backing) is var c && c > 0 ? c : selected.MaxInputTokens;
        var modelSizeB = ParseModelSizeBillions(backing);
        var warnings = new List<string>();
        var recommendations = new List<string>();

        var isSimpleTask = approxTokens < 1500 && toolCount <= 2 && imageCount == 0 && messageCount <= 4;
        var isComplexTask = approxTokens > 12000 || imageCount > 0 || messageCount >= 10 || (toolCount >= 8 && approxTokens > 4000);

        if (imageCount > 0 && backing.Capabilities?.Vision != true)
        {
            warnings.Add($"This request includes {imageCount} image input{(imageCount == 1 ? "" : "s")}, but {GetDisplayName(backing)} does not advertise vision support.");
            if (GetProfileRecommendation("vision") is { } vp) recommendations.Add($"Use the {vp} profile for image-based tasks.");
        }

        if (isComplexTask && toolCount >= 8 && backing.Capabilities?.TrainedForToolUse != true)
        {
            warnings.Add($"{GetDisplayName(backing)} does not advertise tool-use training, but this request still exposes {toolCount} tools.");
            if (GetProfileRecommendation("toolUse") is { } tp) recommendations.Add($"Use the {tp} profile for tool-heavy requests.");
        }

        if (contextLimit > 0 && approxTokens > contextLimit * 0.7)
        {
            warnings.Add($"This request is estimated at about {approxTokens:N0} tokens, which is close to the model context limit of {contextLimit:N0} tokens.");
            recommendations.Add("Split the task into smaller steps or reduce attached context before retrying.");
        }

        if (isSimpleTask && modelSizeB is >= 30)
        {
            warnings.Add($"{GetDisplayName(backing)} looks oversized for this simple request ({modelSizeB.Value:F1}B-class model on a low-complexity task).");
            if (GetProfileRecommendation("quick") is { } qp) recommendations.Add($"Use the {qp} profile for quick edits, lookups, and short responses.");
        }

        if (isComplexTask && modelSizeB is <= 10)
        {
            warnings.Add($"{GetDisplayName(backing)} may be undersized for this more complex request ({modelSizeB.Value:F1}B-class model with {toolCount} tools and about {approxTokens:N0} tokens).");
            if (GetProfileRecommendation("planning") is { } pp) recommendations.Add($"Use the {pp} profile for larger synthesis, architecture, or long-context work.");
        }

        var quickRec = GetProfileRecommendation("quick");
        var planningRec = GetProfileRecommendation("planning");
        if (isComplexTask && quickRec != null && planningRec != null)
        {
            recommendations.Add($"Consider splitting the workflow: use {quickRec} for search/tool steps, then {planningRec} for synthesis or final answers.");
        }

        if (warnings.Count == 0 && recommendations.Count == 0) return null;

        var lines = new List<string> { "[LM Studio advisor]" };
        lines.AddRange(warnings.Select(w => $"- {w}"));
        lines.AddRange(recommendations.Select(r => $"- {r}"));
        return string.Join('\n', lines) + "\n\n";
    }

    // ──────────────────────────────────────────────────────────────────────
    // Tool conversion / budgeting
    // ──────────────────────────────────────────────────────────────────────

    private List<ChatTool> ConvertTools(List<ToolSchema>? tools, List<ChatMessage>? messages, int? tokenBudget)
    {
        if (tools == null || tools.Count == 0) return new();

        var config = _getConfig();
        var usedToolNames = new HashSet<string>();
        if (messages != null)
        {
            foreach (var m in messages)
            {
                if (m.ToolCalls != null)
                {
                    foreach (var tc in m.ToolCalls) usedToolNames.Add(tc.Function.Name);
                }
            }
        }

        var scored = tools.Select(t =>
        {
            double score = 0;
            if (usedToolNames.Contains(t.Name)) score += 1000;
            if (t.Name.StartsWith("mbt_lmstudio_", StringComparison.Ordinal)) score += 100;
            score -= Regex.Matches(t.Name, "_").Count * 5;
            score -= (t.Name.Length / 10) * 3;
            return (Tool: t, Score: score);
        })
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Tool.Name, StringComparer.Ordinal)
        .Select(x => x.Tool)
        .ToList();

        var selected = scored;
        if (config.MaxTools > 0 && selected.Count > config.MaxTools)
        {
            var dropped = selected.Count - config.MaxTools;
            selected = selected.Take(config.MaxTools).ToList();
            Log($"Tool budget (count): {tools.Count} available -> sending {selected.Count} (dropped {dropped})");
        }

        if (tokenBudget.HasValue)
        {
            var kept = new List<ToolSchema>();
            var used = 0;
            foreach (var tool in selected)
            {
                var toolTokens = EstimateToolTokens(tool);
                if (used + toolTokens > tokenBudget.Value) break;
                kept.Add(tool);
                used += toolTokens;
            }
            if (kept.Count < selected.Count)
            {
                Log($"Tool budget (tokens): {selected.Count} available -> sending {kept.Count} (~{used} tokens, budget {tokenBudget})");
            }
            selected = kept;
        }

        if (selected.Count < tools.Count)
        {
            Log($"Kept: {string.Join(", ", selected.Select(t => t.Name))}");
        }

        return selected.Select(t => new ChatTool
        {
            Type = "function",
            Function = new ChatToolFunction { Name = t.Name, Description = t.Description, Parameters = t.InputSchema },
        }).ToList();
    }

    // ──────────────────────────────────────────────────────────────────────
    // LM Studio Auto: per-conversation task-profile routing
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the most recent routing directive already in this conversation's history — either a
    /// prior classification note (see the note built in ResolveAutoModelIdAsync) or a later
    /// mbt_lmstudio_switch_model tool result (see InvokeSwitchModelTool) — so a multi-turn chat
    /// sticks with (or updates to) that model instead of re-classifying, and potentially
    /// reload-thrashing, on every turn. Scans from the end so a later switch always wins over an
    /// earlier classification.
    /// </summary>
    private static string? FindLatestRoutedModelId(List<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (m.Role != "assistant" && m.Role != "tool") continue;
            var text = ExtractMessageText(m);

            var switchMatch = SwitchModelRe.Match(text);
            if (switchMatch.Success) return switchMatch.Groups[1].Value;

            var autoMatch = AutoRoutedRe.Match(text);
            if (autoMatch.Success) return autoMatch.Groups[1].Value;
        }
        return null;
    }

    private string? ResolveProfileModelId(string profileKey)
    {
        var configured = GetConfiguredTaskTypeModels();
        if (configured.TryGetValue(profileKey, out var id) && GetModelById(id) != null) return id;
        return SelectSuggestedModel(profileKey)?.Id;
    }

    /// <summary>Implements mbt_lmstudio_switch_model — only ever offered to the model when LM Studio Auto is selected (see the availableTools filter in ChatStreamAsync).</summary>
    public string InvokeSwitchModelTool(JsonObject input)
    {
        var profileKey = input["profile"]?.GetValue<string>()?.Trim() ?? "";
        if (!TaskProfileKeys.Contains(profileKey))
        {
            return $"Unknown profile \"{profileKey}\". Valid profiles: {string.Join(", ", TaskProfileKeys)}.";
        }

        var modelId = ResolveProfileModelId(profileKey);
        if (modelId == null)
        {
            return $"No available model found for the \"{profileKey}\" profile; staying on the current model.";
        }

        var model = GetModelById(modelId)!;
        var reason = input["reason"]?.GetValue<string>()?.Trim();
        Log($"LM Studio Auto: model requested a switch to {TaskProfileLabels[profileKey]} ({GetDisplayName(model)})" +
            (string.IsNullOrEmpty(reason) ? "" : $" — {reason}"));

        return $"[LM Studio switched to {modelId}] Switched to {TaskProfileLabels[profileKey]} ({GetDisplayName(model)}). " +
               "This and all following responses in this conversation will use that model.";
    }

    /// <summary>Cheap fallback classifier used when no model is loaded to ask, or the advisor call fails/returns garbage.</summary>
    private static string HeuristicClassifyProfile(List<ChatMessage> messages, List<ToolSchema>? tools)
    {
        if (CountImages(messages) > 0) return "vision";

        var toolCount = tools?.Count ?? 0;
        var approxTokens = EstimateRequestTokens(messages);
        var lastUserText = messages.LastOrDefault(m => m.Role == "user") is { } lastUser
            ? ExtractMessageText(lastUser).ToLowerInvariant() : "";

        if (Regex.IsMatch(lastUserText, @"\breview\b|\baudit\b|\bcheck (this|my) (code|work)\b")) return "review";
        if (approxTokens < 1500 && toolCount <= 2 && messages.Count <= 4) return "quick";
        if (toolCount >= 8) return "toolUse";
        if (approxTokens > 12000 || messages.Count >= 10) return "planning";
        return "coding";
    }

    /// <summary>Asks a loaded model to classify the request into one of the task profiles; falls back to the cheap heuristic on any failure.</summary>
    private async Task<string> ClassifyProfileAsync(List<ChatMessage> messages, List<ToolSchema>? tools, CancellationToken ct)
    {
        var advisor = SelectAdvisorModel();
        if (advisor == null) return HeuristicClassifyProfile(messages, tools);

        var lastUserText = messages.LastOrDefault(m => m.Role == "user") is { } lastUser ? ExtractMessageText(lastUser) : "";
        var profiles = string.Join('\n', TaskProfileKeys.Select(k => $"- \"{k}\": {TaskProfileDescriptions[k]}"));
        var prompt = string.Join('\n',
            "Classify the following coding-assistant request into exactly one task profile.",
            "Task profiles:", profiles, "",
            $"The request has {CountImages(messages)} image(s) attached and exposes {tools?.Count ?? 0} tool(s).",
            "Most recent user message:",
            lastUserText.Length > 2000 ? lastUserText[..2000] : lastUserText,
            "",
            "Respond with ONLY a JSON object like {\"profile\": \"coding\"}. No prose, no markdown fences.");

        var requestId = $"auto-model-classify-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var text = "";
        try
        {
            var classifyMessages = new List<ChatMessage> { new() { Role = "user", Content = JsonValue.Create(prompt) } };
            await foreach (var chunk in _client.StreamChatCompletionAsync(advisor.Id, classifyMessages, new ChatStreamOptions { Temperature = 0.1, MaxTokens = 60 }, requestId, cts.Token))
            {
                text += chunk;
            }
        }
        catch (Exception e)
        {
            Warn($"LM Studio Auto classification request failed, falling back to heuristic: {e.Message}");
            return HeuristicClassifyProfile(messages, tools);
        }

        var match = Regex.Match(text, "\"profile\"\\s*:\\s*\"(\\w+)\"", RegexOptions.IgnoreCase);
        var key = match.Success ? match.Groups[1].Value.ToLowerInvariant() : "";
        if (TaskProfileKeys.Contains(key)) return key;

        Warn($"LM Studio Auto classification returned an unrecognized profile ({(text.Length > 120 ? text[..120] : text)}); falling back to heuristic.");
        return HeuristicClassifyProfile(messages, tools);
    }

    /// <summary>Resolves the "LM Studio Auto" virtual selection to a concrete model id for this request, reusing a prior pick from earlier in the same conversation when one exists.</summary>
    private async Task<(string ModelId, string? Note)> ResolveAutoModelIdAsync(List<ChatMessage> messages, List<ToolSchema>? tools, CancellationToken ct)
    {
        var routed = FindLatestRoutedModelId(messages);
        if (routed != null && GetModelById(routed) != null)
        {
            return (routed, null);
        }

        var profileKey = await ClassifyProfileAsync(messages, tools, ct);
        var modelId = ResolveProfileModelId(profileKey);
        if (modelId == null)
        {
            throw new Exception("LM Studio Auto could not find any available model to route this request to.");
        }

        var model = GetModelById(modelId)!;
        var note = $"[LM Studio Auto → {modelId}] Routed to {TaskProfileLabels[profileKey]} ({GetDisplayName(model)}) for this conversation.\n\n";
        return (modelId, note);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Chat response orchestration (the core of provideLanguageModelChatResponse)
    // ──────────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<StreamPart> ChatStreamAsync(
        ChatStreamRequest request,
        string requestId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_advertisedById.TryGetValue(request.SelectedId, out var selected))
        {
            // getAdvertisedModels() hasn't been called yet for this id (e.g. after a config
            // change) — recompute once before giving up.
            GetAdvertisedModels();
            if (!_advertisedById.TryGetValue(request.SelectedId, out selected))
            {
                throw new Exception($"Unknown model selection: {request.SelectedId}");
            }
        }

        // mbt_lmstudio_switch_model lets the model hand the rest of the conversation off to a
        // better-fitting model — only meaningful (and only offered) under LM Studio Auto, since a
        // directly-pinned model selection should stay pinned rather than silently answering from
        // somewhere else while the picker still shows the original pick.
        var availableTools = selected.Id == AutoModelId
            ? request.Tools
            : request.Tools?.Where(t => t.Name != SwitchModelTool.Name).ToList();

        var modelId = selected.LmstudioModelId;
        string? autoNote = null;
        if (selected.Id == AutoModelId)
        {
            if (!await _client.CheckConnectionAsync())
            {
                throw new Exception($"Cannot reach the LM Studio server at {_getConfig().ServerUrl}; LM Studio Auto cannot classify or route this request. Make sure LM Studio is running.");
            }
            (modelId, autoNote) = await ResolveAutoModelIdAsync(request.Messages, availableTools, ct);
        }

        Log($"Chat request for {modelId} | {request.Messages.Count} messages | {availableTools?.Count ?? 0} tools" +
            (selected.Id == AutoModelId ? " (via LM Studio Auto)" : ""));

        var config = _getConfig();
        var outputReserveEstimate = Math.Min(request.ModelOptions?.MaxOutputTokens ?? config.MaxOutputTokens, ContextReserveCap);
        var toolTokensEstimate = (availableTools ?? new()).Sum(EstimateToolTokens);
        var requiredContextLength = EstimateRequestTokens(request.Messages) + toolTokensEstimate + outputReserveEstimate + 512;

        var ready = await _client.EnsureModelLoadedAsync(modelId, requiredContextLength);
        if (!ready)
        {
            throw new Exception($"LM Studio could not load the selected model: {modelId}");
        }

        // ensureModelLoaded may have unloaded/reloaded the model with a different context
        // length — refresh so backingModel reflects it.
        await RefreshModelsAsync();
        GetAdvertisedModels();

        var backingModel = GetModelById(modelId);

        var openaiMessages = new List<ChatMessage>(request.Messages);

        var injectSystemPrompt = config.InjectSystemPrompt;
        var hasLeadingSystem = openaiMessages.Count > 0 && openaiMessages[0].Role == "system";
        if (injectSystemPrompt && !hasLeadingSystem)
        {
            openaiMessages.Insert(0, new ChatMessage
            {
                Role = "system",
                Content = JsonValue.Create(string.Join('\n',
                    "You are a helpful AI coding assistant embedded in Visual Studio Code.",
                    "Respond concisely and directly. Answer in the same language the user writes in.",
                    "Do not introduce yourself. Do not restate your identity or capabilities unless the user explicitly asks.",
                    "Do not include <think> or </think> tags in your output.")),
            });
        }
        else
        {
            Log($"Skipping system prompt (inject={injectSystemPrompt}, leadingSystem={hasLeadingSystem})");
        }

        var contextLimit = GetEffectiveContextLength(backingModel) is var cl && cl > 0 ? cl : selected.MaxInputTokens;
        var outputReserve = Math.Min(request.ModelOptions?.MaxOutputTokens ?? config.MaxOutputTokens, ContextReserveCap);
        var messageTokens = EstimateRequestTokens(request.Messages);

        int? toolBudget = null;
        if (contextLimit > 0)
        {
            var safetyMargin = Math.Max(256, (int)(contextLimit * 0.02));
            var available = contextLimit - messageTokens - outputReserve - safetyMargin;
            if (available <= 0)
            {
                var displayName = backingModel != null ? GetDisplayName(backingModel) : selected.Name;
                throw new Exception(
                    $"This request (~{messageTokens:N0} tokens) plus {outputReserve:N0} reserved output tokens exceeds {displayName}'s context length of {contextLimit:N0} tokens, even before any tools are added. Shorten the conversation, lower max output tokens, or pick a model with a larger context window.");
            }
            toolBudget = available;
        }

        var tools = ConvertTools(availableTools, request.Messages, toolBudget);
        if (autoNote != null)
        {
            yield return StreamPart.OfText(autoNote);
        }
        var advisory = BuildModelAdvisory(selected, backingModel, request.Messages, request.ModelOptions, tools);
        if (advisory != null)
        {
            yield return StreamPart.OfText(advisory);
        }

        var dedup = new HashSet<string>();
        var emittedAny = false;

        var stream = _client.StreamChatCompletionWithToolsAsync(
            modelId, openaiMessages,
            new ChatStreamOptions { Temperature = request.ModelOptions?.Temperature, MaxTokens = request.ModelOptions?.MaxOutputTokens, Tools = tools },
            requestId, ct);

        await foreach (var part in stream)
        {
            if (ct.IsCancellationRequested) break;

            if (part.Text != null)
            {
                emittedAny = true;
                yield return part;
            }
            else if (part.ToolCall != null)
            {
                var tc = part.ToolCall;
                if (string.IsNullOrEmpty(tc.Id) || string.IsNullOrEmpty(tc.Function.Name))
                {
                    Log($"Skipping incomplete tool call: id={tc.Id} name={tc.Function.Name}");
                    continue;
                }

                var dedupKey = $"{tc.Function.Name}:{tc.Function.Arguments}";
                if (!dedup.Add(dedupKey))
                {
                    Log($"Duplicate tool call blocked (same response): {tc.Function.Name}");
                    continue;
                }

                emittedAny = true;
                Log($"Tool call: {tc.Function.Name} (id={tc.Id})");
                yield return part;
            }
        }

        if (!emittedAny && !ct.IsCancellationRequested)
        {
            var displayName = backingModel != null ? GetDisplayName(backingModel) : selected.Name;
            var loadedContext = GetEffectiveContextLength(backingModel);
            throw new Exception(string.Join('\n',
                $"LM Studio closed the connection without returning any content for {displayName}.",
                $"This request was estimated at ~{messageTokens:N0} tokens against a loaded context of {(loadedContext > 0 ? loadedContext.ToString("N0") : "unknown")} tokens.",
                "",
                "To fix this in LM Studio:",
                $"1. Eject/unload {displayName} (Developer tab, or the model dropdown in the chat sidebar).",
                $"2. Reload it and raise \"Context Length\" (n_ctx) in the load settings — set it well above {messageTokens:N0} tokens, e.g. round up to the next power of 2 your hardware/VRAM can hold (8192 -> 16384 -> 32768, etc.).",
                "3. If loading via the API/CLI, pass a larger contextLength in the load request instead of relying on the default.",
                "Alternatively, shorten the conversation (start a new chat) so fewer tokens need to fit in the current context."));
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static string FormatModelName(string modelId)
    {
        var stripped = Regex.Replace(modelId, @"^(models/|/)", "");
        var spaced = Regex.Replace(stripped, @"[-_]", " ");
        var noExt = Regex.Replace(spaced, @"\.gguf$", "", RegexOptions.IgnoreCase);
        var collapsed = Regex.Replace(noExt, @"\s+", " ").Trim();
        return string.Join(' ', collapsed.Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string GetDisplayName(LmStudioModel model) =>
        !string.IsNullOrWhiteSpace(model.DisplayName) ? model.DisplayName.Trim() : FormatModelName(model.Id);
}
