import * as vscode from 'vscode';
import { BackendProcess } from '../backend-process';
import { Logger } from '../logger';

function makeResult(text: string): vscode.LanguageModelToolResult {
  return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(text)]);
}

function workspaceRoot(): string {
  return vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath ?? process.cwd();
}

// ─── read_file ────────────────────────────────────────────────────────────────

export const READ_FILE_TOOL_NAME = 'mbt_lmstudio_read_file';

interface ReadFileInput {
  path: string;
  startLine?: number;
  endLine?: number;
}

export function createReadFileTool(backend: BackendProcess, logger: Logger): vscode.LanguageModelTool<ReadFileInput> {
  return {
    prepareInvocation: (options) => ({
      invocationMessage: `Reading file: ${options.input.path}`,
    }),

    invoke: async (options) => {
      logger.verbose(`[read_file] ${options.input.path}`);
      const result = await backend.invokeTool({
        name: READ_FILE_TOOL_NAME,
        input: { path: options.input.path, startLine: options.input.startLine, endLine: options.input.endLine },
        workspaceRoot: workspaceRoot(),
        tokenBudget: options.tokenizationOptions?.tokenBudget,
      });
      return makeResult(result);
    },
  };
}

// ─── write_file ───────────────────────────────────────────────────────────────

export const WRITE_FILE_TOOL_NAME = 'mbt_lmstudio_write_file';

interface WriteFileInput {
  path: string;
  content: string;
  createDirectories?: boolean;
}

export function createWriteFileTool(backend: BackendProcess, logger: Logger): vscode.LanguageModelTool<WriteFileInput> {
  return {
    prepareInvocation: (options) => ({
      invocationMessage: `Writing file: ${options.input.path}`,
    }),

    invoke: async (options) => {
      logger.verbose(`[write_file] ${options.input.path}`);
      const result = await backend.invokeTool({
        name: WRITE_FILE_TOOL_NAME,
        input: { path: options.input.path, content: options.input.content, createDirectories: options.input.createDirectories },
        workspaceRoot: workspaceRoot(),
      });
      return makeResult(result);
    },
  };
}

// ─── list_directory ───────────────────────────────────────────────────────────

export const LIST_DIRECTORY_TOOL_NAME = 'mbt_lmstudio_list_directory';

interface ListDirectoryInput {
  path?: string;
  recursive?: boolean;
  maxDepth?: number;
}

export function createListDirectoryTool(backend: BackendProcess, logger: Logger): vscode.LanguageModelTool<ListDirectoryInput> {
  return {
    prepareInvocation: (options) => ({
      invocationMessage: `Listing: ${options.input.path ?? 'workspace root'}`,
    }),

    invoke: async (options) => {
      logger.verbose(`[list_directory] ${options.input.path ?? '.'}`);
      const result = await backend.invokeTool({
        name: LIST_DIRECTORY_TOOL_NAME,
        input: { path: options.input.path, recursive: options.input.recursive, maxDepth: options.input.maxDepth },
        workspaceRoot: workspaceRoot(),
        tokenBudget: options.tokenizationOptions?.tokenBudget,
      });
      return makeResult(result);
    },
  };
}

// ─── search_files ─────────────────────────────────────────────────────────────

export const SEARCH_FILES_TOOL_NAME = 'mbt_lmstudio_search_files';

interface SearchFilesInput {
  pattern: string;
  glob?: string;
  isRegex?: boolean;
  caseSensitive?: boolean;
  maxResults?: number;
  contextLines?: number;
}

export function createSearchFilesTool(backend: BackendProcess, logger: Logger): vscode.LanguageModelTool<SearchFilesInput> {
  return {
    prepareInvocation: (options) => ({
      invocationMessage: `Searching for: ${options.input.pattern}`,
    }),

    invoke: async (options) => {
      const pattern = options.input.pattern?.trim();
      if (!pattern) {
        return makeResult('No search pattern provided.');
      }
      logger.verbose(`[search_files] pattern="${pattern}"`);
      const result = await backend.invokeTool({
        name: SEARCH_FILES_TOOL_NAME,
        input: {
          pattern,
          glob: options.input.glob,
          isRegex: options.input.isRegex,
          caseSensitive: options.input.caseSensitive,
          maxResults: options.input.maxResults,
          contextLines: options.input.contextLines,
        },
        workspaceRoot: workspaceRoot(),
        tokenBudget: options.tokenizationOptions?.tokenBudget,
      });
      return makeResult(result);
    },
  };
}
