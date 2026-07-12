import * as vscode from 'vscode';
import { BackendProcess } from '../backend-process';
import { Logger } from '../logger';
import { CONFIG_SECTION } from '../constants';

export const TERMINAL_TOOL_NAME = 'mbt_lmstudio_run_in_terminal';

interface TerminalToolInput {
  command: string;
  cwd?: string;
}

function getWorkspaceCwd(): string {
  return vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath ?? process.cwd();
}

function makeResult(text: string): vscode.LanguageModelToolResult {
  return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(text)]);
}

/**
 * Shows the command in a visible integrated terminal for the user (VS Code UI, must stay in the
 * shell), while the backend actually executes it and captures stdout/stderr/exit code.
 */
export function createTerminalTool(backend: BackendProcess, logger: Logger): vscode.LanguageModelTool<TerminalToolInput> {
  return {
    prepareInvocation: (options) => ({
      invocationMessage: `Running: ${options.input.command}`,
    }),

    invoke: async (options, token) => {
      const command = options.input.command?.trim();
      if (!command) {
        return makeResult('No command provided.');
      }
      if (token.isCancellationRequested) {
        return makeResult('Cancelled.');
      }

      const cwd = options.input.cwd?.trim() || getWorkspaceCwd();
      logger.verbose(`[run_in_terminal] cwd=${cwd}  cmd=${command}`);

      const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
      const terminalName = config.get<string>('terminalToolName', 'LM Studio Tool Terminal');
      const existing = vscode.window.terminals.find((t) => t.name === terminalName && t.exitStatus === undefined);
      const terminal = existing ?? vscode.window.createTerminal({ name: terminalName });
      terminal.show(true);
      terminal.sendText(command, true);

      const result = await backend.invokeTool({
        name: TERMINAL_TOOL_NAME,
        input: { command, cwd },
        workspaceRoot: getWorkspaceCwd(),
        tokenBudget: options.tokenizationOptions?.tokenBudget,
      });

      return makeResult(result);
    },
  };
}
