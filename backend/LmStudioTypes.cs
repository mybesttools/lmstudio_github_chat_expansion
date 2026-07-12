using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LmStudioBackend;

/// <summary>Configuration mirrored from the VS Code shell's settings (see src/types.ts LMStudioConfig plus the tool/chat settings previously read via vscode.workspace.getConfiguration).</summary>
public sealed class LmStudioConfig
{
    public string ServerUrl { get; set; } = "http://localhost:1234";
    public string ApiKey { get; set; } = "";
    public int RequestTimeoutMs { get; set; } = 60000;
    public int ModelIdleTtl { get; set; } = 0;

    public int MaxInputTokens { get; set; } = 131072;
    public int MaxOutputTokens { get; set; } = 16384;
    public int MaxTools { get; set; } = 20;
    public bool EnableToolCalling { get; set; } = true;
    public bool InjectSystemPrompt { get; set; } = true;
    public bool EnableThinking { get; set; } = true;
    public string ReasoningEffort { get; set; } = "default";
    public bool EnableModelAdvisories { get; set; } = true;
    public bool EnableAutoModel { get; set; } = true;
    public Dictionary<string, string> TaskTypeModels { get; set; } = new();

    public bool EnableTerminalTool { get; set; } = true;
    public string TerminalToolName { get; set; } = "LM Studio Tool Terminal";
    public int TerminalToolTimeoutMs { get; set; } = 30000;
}

public sealed class LoadedInstanceConfig
{
    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }
}

public sealed class LoadedInstance
{
    public string Id { get; set; } = "";
    public LoadedInstanceConfig? Config { get; set; }
}

public sealed class ModelCapabilities
{
    public bool? Vision { get; set; }

    [JsonPropertyName("trained_for_tool_use")]
    public bool? TrainedForToolUse { get; set; }
}

/// <summary>Normalized model shape used everywhere after getModels(), mirroring types.ts LMStudioModel.</summary>
public sealed class LmStudioModel
{
    public string Id { get; set; } = "";
    public string Object { get; set; } = "model";
    public string OwnedBy { get; set; } = "unknown";
    public bool? Loaded { get; set; }
    public long? SizeBytes { get; set; }
    public List<LoadedInstance>? LoadedInstances { get; set; }
    public string? ParamsString { get; set; }
    public string? Type { get; set; }
    public string? Publisher { get; set; }
    public string? DisplayName { get; set; }
    public string? Architecture { get; set; }
    public int? MaxContextLength { get; set; }
    public ModelCapabilities? Capabilities { get; set; }
}

/// <summary>Raw shape from LM Studio's /api/v1/models endpoint (uses "key" instead of "id", snake_case fields).</summary>
public sealed class LmStudioRawModel
{
    public string Key { get; set; } = "";
    public string? Type { get; set; }
    public string? Publisher { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
    public string? Architecture { get; set; }

    [JsonPropertyName("max_context_length")]
    public int? MaxContextLength { get; set; }

    [JsonPropertyName("size_bytes")]
    public long? SizeBytes { get; set; }

    [JsonPropertyName("params_string")]
    public string? ParamsString { get; set; }

    [JsonPropertyName("loaded_instances")]
    public List<LoadedInstance>? LoadedInstances { get; set; }
    public ModelCapabilities? Capabilities { get; set; }
}

public sealed class LoadModelConfig
{
    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }
}

public sealed class LoadModelResponse
{
    public string Type { get; set; } = "";

    [JsonPropertyName("instance_id")]
    public string InstanceId { get; set; } = "";
    public string Status { get; set; } = "";

    [JsonPropertyName("load_config")]
    public LoadModelConfig? LoadConfig { get; set; }
}

/// <summary>Content part for multi-modal chat messages (text or image_url), mirrors types.ts ChatMessageContentPart.</summary>
public sealed class ChatMessageContentPart
{
    public string Type { get; set; } = "text"; // "text" | "image_url"
    public string? Text { get; set; }
    public ImageUrl? ImageUrl { get; set; }
}

public sealed class ImageUrl
{
    public string Url { get; set; } = "";
}

public sealed class ToolCallFunction
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "function";
    public ToolCallFunction Function { get; set; } = new();
}

/// <summary>Chat message on the wire. Content is JsonNode so it can hold string, array-of-parts, or null (OpenAI requires null when an assistant message only has tool_calls).</summary>
public sealed class ChatMessage
{
    public string Role { get; set; } = "user"; // system | user | assistant | tool
    public JsonNode? Content { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ToolCall>? ToolCalls { get; set; }
}

public sealed class ChatToolFunction
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonNode? Parameters { get; set; }
}

/// <summary>Tool schema in OpenAI function-calling format, also used as the wire shape VS Code's LanguageModelChatTool[] is translated into.</summary>
public sealed class ChatTool
{
    public string Type { get; set; } = "function";
    public ChatToolFunction Function { get; set; } = new();
}
