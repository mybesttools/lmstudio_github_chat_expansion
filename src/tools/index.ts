import * as vscode from 'vscode';
import { BackendProcess } from '../backend-process';
import { Logger } from '../logger';
import { TERMINAL_TOOL_NAME, createTerminalTool } from './terminal-tool';
import {
  READ_FILE_TOOL_NAME,
  WRITE_FILE_TOOL_NAME,
  LIST_DIRECTORY_TOOL_NAME,
  SEARCH_FILES_TOOL_NAME,
  createReadFileTool,
  createWriteFileTool,
  createListDirectoryTool,
  createSearchFilesTool,
} from './file-tools';
import { CODEBASE_INDEX_TOOL_NAME, createCodebaseIndexTool } from './codebase-index-tool';
import { SWITCH_MODEL_TOOL_NAME, createSwitchModelTool } from './switch-model-tool';

export {
  TERMINAL_TOOL_NAME,
  READ_FILE_TOOL_NAME,
  WRITE_FILE_TOOL_NAME,
  LIST_DIRECTORY_TOOL_NAME,
  SEARCH_FILES_TOOL_NAME,
  CODEBASE_INDEX_TOOL_NAME,
  SWITCH_MODEL_TOOL_NAME,
};

/**
 * Registers all LM tools with the extension context.
 * Each tool must also have a matching entry in package.json contributes.languageModelTools.
 */
export function registerAllTools(
  context: vscode.ExtensionContext,
  backend: BackendProcess,
  logger: Logger
): void {
  context.subscriptions.push(
    vscode.lm.registerTool(TERMINAL_TOOL_NAME, createTerminalTool(backend, logger)),
    vscode.lm.registerTool(READ_FILE_TOOL_NAME, createReadFileTool(backend, logger)),
    vscode.lm.registerTool(WRITE_FILE_TOOL_NAME, createWriteFileTool(backend, logger)),
    vscode.lm.registerTool(LIST_DIRECTORY_TOOL_NAME, createListDirectoryTool(backend, logger)),
    vscode.lm.registerTool(SEARCH_FILES_TOOL_NAME, createSearchFilesTool(backend, logger)),
    vscode.lm.registerTool(CODEBASE_INDEX_TOOL_NAME, createCodebaseIndexTool(backend, logger)),
    vscode.lm.registerTool(SWITCH_MODEL_TOOL_NAME, createSwitchModelTool(backend, logger))
  );

  logger.info(
    `✅ Registered LM tools: ${
      [
        TERMINAL_TOOL_NAME,
        READ_FILE_TOOL_NAME,
        WRITE_FILE_TOOL_NAME,
        LIST_DIRECTORY_TOOL_NAME,
        SEARCH_FILES_TOOL_NAME,
        CODEBASE_INDEX_TOOL_NAME,
        SWITCH_MODEL_TOOL_NAME,
      ].join(', ')
    }`
  );
}
