import * as vscode from 'vscode';
import { BackendProcess } from '../backend-process';
import { Logger } from '../logger';

function makeResult(text: string): vscode.LanguageModelToolResult {
  return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(text)]);
}

function workspaceRoot(): string {
  return vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath ?? process.cwd();
}

export const CODEBASE_INDEX_TOOL_NAME = 'mbt_lmstudio_get_codebase_index';

interface CodebaseIndexInput {
  includeSymbols?: boolean;
  maxFileSizeKB?: number;
}

export function createCodebaseIndexTool(backend: BackendProcess, logger: Logger): vscode.LanguageModelTool<CodebaseIndexInput> {
  return {
    prepareInvocation: () => ({
      invocationMessage: 'Indexing codebase…',
    }),

    invoke: async (options) => {
      logger.verbose(`[get_codebase_index] root="${workspaceRoot()}"`);
      const result = await backend.invokeTool({
        name: CODEBASE_INDEX_TOOL_NAME,
        input: { includeSymbols: options.input.includeSymbols, maxFileSizeKB: options.input.maxFileSizeKB },
        workspaceRoot: workspaceRoot(),
        tokenBudget: options.tokenizationOptions?.tokenBudget,
      });
      return makeResult(result);
    },
  };
}
