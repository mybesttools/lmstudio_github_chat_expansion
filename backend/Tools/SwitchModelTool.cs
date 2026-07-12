namespace LmStudioBackend.Tools;

/// <summary>
/// Name constant only. Unlike the other tools dispatched through ToolInvoker, the switch-model
/// tool needs access to the live model catalog and task-profile config, so its implementation
/// lives on ChatOrchestrator (see InvokeSwitchModelTool) and Program.cs routes calls there
/// directly instead of through ToolInvoker.
/// </summary>
public static class SwitchModelTool
{
    public const string Name = "mbt_lmstudio_switch_model";
}
