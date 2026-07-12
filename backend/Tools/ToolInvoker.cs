using System.Text.Json.Nodes;

namespace LmStudioBackend.Tools;

public sealed class InvokeToolRequest
{
    public string Name { get; set; } = "";
    public JsonObject Input { get; set; } = new();
    public string WorkspaceRoot { get; set; } = "";
    public int? TokenBudget { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Dispatches invokeTool IPC calls to the appropriate tool implementation, mirroring src/tools/index.ts's registration list.</summary>
public static class ToolInvoker
{
    public static async Task<string> InvokeAsync(InvokeToolRequest request, int terminalTimeoutMs, CancellationToken ct)
    {
        var input = request.Input;
        switch (request.Name)
        {
            case TerminalTool.Name:
                if (!request.Enabled)
                {
                    return "Terminal tool is disabled (lmstudio-copilot-expansion.enableTerminalTool = false).";
                }
                return await TerminalTool.InvokeAsync(
                    input["command"]?.GetValue<string>() ?? "",
                    input["cwd"]?.GetValue<string>()?.Trim() is { Length: > 0 } cwd ? cwd : request.WorkspaceRoot,
                    terminalTimeoutMs,
                    request.TokenBudget,
                    ct);

            case FileTools.ReadFileName:
                return FileTools.ReadFile(
                    request.WorkspaceRoot,
                    input["path"]!.GetValue<string>(),
                    input["startLine"]?.GetValue<int>(),
                    input["endLine"]?.GetValue<int>(),
                    request.TokenBudget);

            case FileTools.WriteFileName:
                return FileTools.WriteFile(
                    request.WorkspaceRoot,
                    input["path"]!.GetValue<string>(),
                    input["content"]?.GetValue<string>() ?? "",
                    input["createDirectories"]?.GetValue<bool>() ?? true);

            case FileTools.ListDirectoryName:
                return FileTools.ListDirectory(
                    request.WorkspaceRoot,
                    input["path"]?.GetValue<string>(),
                    input["recursive"]?.GetValue<bool>() ?? false,
                    input["maxDepth"]?.GetValue<int>(),
                    request.TokenBudget);

            case FileTools.SearchFilesName:
                return await FileTools.SearchFilesAsync(
                    request.WorkspaceRoot,
                    input["pattern"]!.GetValue<string>(),
                    input["glob"]?.GetValue<string>(),
                    input["isRegex"]?.GetValue<bool>() ?? false,
                    input["caseSensitive"]?.GetValue<bool>() ?? false,
                    input["maxResults"]?.GetValue<int>(),
                    input["contextLines"]?.GetValue<int>(),
                    request.TokenBudget);

            case CodebaseIndexTool.Name:
                return CodebaseIndexTool.GetIndex(
                    request.WorkspaceRoot,
                    input["includeSymbols"]?.GetValue<bool>() ?? true,
                    input["maxFileSizeKB"]?.GetValue<int>(),
                    request.TokenBudget);

            default:
                throw new Exception($"Unknown tool: {request.Name}");
        }
    }
}
