using System.Text.Json.Nodes;

namespace LmStudioBackend;

/// <summary>Tool schema as advertised by VS Code (vscode.LanguageModelChatTool: name/description/inputSchema), sent over the wire before budgeting.</summary>
public sealed class ToolSchema
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonNode? InputSchema { get; set; }
}

/// <summary>A model entry as advertised to VS Code's model picker. Mirrors the LMStudioModelInfo shape from lmstudio-provider.ts.</summary>
public sealed class AdvertisedModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Family { get; set; } = "lmstudio";
    public string Version { get; set; } = "1.0.0";
    public int MaxInputTokens { get; set; }
    public int MaxOutputTokens { get; set; }
    public string LmstudioModelId { get; set; } = "";
    public bool IsLoaded { get; set; }
    public bool IsUserSelectable { get; set; } = true;
    public bool ToolCalling { get; set; }
    public bool ImageInput { get; set; }
    public string? TaskProfileKey { get; set; }
}

public sealed class ChatModelOptions
{
    public double? Temperature { get; set; }
    public int? MaxOutputTokens { get; set; }
}

/// <summary>Request payload for the chatStream IPC method.</summary>
public sealed class ChatStreamRequest
{
    public string SelectedId { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
    public List<ToolSchema>? Tools { get; set; }
    public ChatModelOptions? ModelOptions { get; set; }
}

public sealed class TaskProfileChange
{
    public string Key { get; set; } = "";
    public string CurrentModelId { get; set; } = "";
    public string CurrentModelName { get; set; } = "";
    public string SuggestedModelId { get; set; } = "";
    public string SuggestedModelName { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool IsMissing { get; set; }
}

public sealed class TaskTypeModelConfigSuggestion
{
    public string Summary { get; set; } = "";
    public List<string> Details { get; set; } = new();
    public Dictionary<string, string> UpdatedTaskTypeModels { get; set; } = new();
    public List<TaskProfileChange> Changes { get; set; } = new();
    public bool HasMissingModels { get; set; }
}

/// <summary>A configured task-profile model that no longer resolves to any model on the connected server.</summary>
public sealed class MissingTaskTypeModel
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string ModelId { get; set; } = "";
}
