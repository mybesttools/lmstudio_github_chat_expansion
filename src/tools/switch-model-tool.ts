import * as vscode from 'vscode';
import { BackendProcess } from '../backend-process';
import { Logger } from '../logger';

function makeResult(text: string): vscode.LanguageModelToolResult {
  return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(text)]);
}

function workspaceRoot(): string {
  return vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath ?? process.cwd();
}

export const SWITCH_MODEL_TOOL_NAME = 'mbt_lmstudio_switch_model';

interface SwitchModelInput {
  profile: string;
  reason?: string;
}

/**
 * Only actually offered to the model when "LM Studio Auto" is selected — the backend filters it
 * out of the tools sent to LM Studio otherwise (see the availableTools filter in
 * ChatOrchestrator.ChatStreamAsync), so a directly-pinned model selection stays pinned.
 */
export function createSwitchModelTool(backend: BackendProcess, logger: Logger): vscode.LanguageModelTool<SwitchModelInput> {
  return {
    prepareInvocation: (options) => ({
      invocationMessage: `Switching to the ${options.input.profile} model…`,
    }),

    invoke: async (options) => {
      logger.verbose(`[switch_model] profile="${options.input.profile}" reason="${options.input.reason ?? ''}"`);
      const result = await backend.invokeTool({
        name: SWITCH_MODEL_TOOL_NAME,
        input: { profile: options.input.profile, reason: options.input.reason },
        workspaceRoot: workspaceRoot(),
      });
      return makeResult(result);
    },
  };
}
