import * as vscode from 'vscode';
import * as path from 'path';
import { LMStudioProvider } from './lmstudio-provider';
import { BackendProcess } from './backend-process';
import { Logger } from './logger';
import { registerAllTools } from './tools/index';
import { BackendConfig } from './types';
import {
  COMMAND_CHECK_CONNECTION,
  COMMAND_REFRESH_MODELS,
  COMMAND_REVIEW_SUGGESTED_CONFIG,
  CONFIG_SECTION,
  ensureByokUtilityModelDefault,
  GLOBAL_STATE_TASK_TYPE_CONFIG_PROMPTED,
  PROVIDER_VENDOR_ID,
} from './constants';

let provider: LMStudioProvider | undefined;
let backend: BackendProcess | undefined;
let registration: vscode.Disposable | undefined;
let outputChannel: vscode.OutputChannel;
let extensionContext: vscode.ExtensionContext | undefined;

function buildBackendConfig(): BackendConfig {
  const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
  return {
    serverUrl: config.get<string>('serverUrl', 'http://localhost:1234'),
    apiKey: config.get<string>('apiKey', ''),
    requestTimeoutMs: config.get<number>('requestTimeout', 60000),
    modelIdleTtl: config.get<number>('modelIdleTtl', 0),
    maxInputTokens: config.get<number>('maxInputTokens', 131072),
    maxOutputTokens: config.get<number>('maxOutputTokens', 16384),
    maxTools: config.get<number>('maxTools', 20),
    enableToolCalling: config.get<boolean>('enableToolCalling', true),
    injectSystemPrompt: config.get<boolean>('injectSystemPrompt', true),
    enableThinking: config.get<boolean>('enableThinking', true),
    reasoningEffort: config.get<string>('reasoningEffort', 'default'),
    enableModelAdvisories: config.get<boolean>('enableModelAdvisories', true),
    enableAutoModel: config.get<boolean>('enableAutoModel', true),
    taskTypeModels: config.get<Record<string, string>>('taskTypeModels', {}),
    enableTerminalTool: config.get<boolean>('enableTerminalTool', true),
    terminalToolName: config.get<string>('terminalToolName', 'LM Studio Tool Terminal'),
    terminalToolTimeoutMs: config.get<number>('terminalToolTimeout', 30000),
    logLevel: config.get<string>('logLevel', 'verbose'),
  };
}

/**
 * @param auto When true, this is the automatic startup check rather than a
 * user-triggered command: skip the soft "could be a better fit" nag if already
 * shown once before (tracked in globalState), and mark it shown after the user
 * answers so it won't ask again. That gate is bypassed whenever a *configured*
 * task-profile model no longer resolves against the connected server at all —
 * that's a broken config, not a soft suggestion, so it's re-checked and
 * re-surfaced on every startup until it's fixed.
 */
async function reviewSuggestedModelConfig(
  refreshFirst: boolean,
  logger: Logger,
  auto = false
): Promise<void> {
  if (!provider) {
    return;
  }

  if (refreshFirst) {
    await provider.refreshModels();
  }

  const missingModels = auto ? await provider.getMissingConfiguredModels() : [];
  if (missingModels.length > 0) {
    logger.warn(
      `[Config advisor] ${missingModels.length} configured task-profile model(s) are no longer on the server: ` +
      missingModels.map((m) => `${m.label} (${m.modelId})`).join(', ')
    );
  }

  if (auto && missingModels.length === 0 && extensionContext?.globalState.get(GLOBAL_STATE_TASK_TYPE_CONFIG_PROMPTED, false)) {
    return;
  }

  logger.info('Asking a loaded model to recommend a task-profile config (falls back to heuristic)...');
  const suggestion = await provider.getTaskTypeModelConfigSuggestion();
  if (!suggestion) {
    const availableModels = await provider.getAvailableModels();
    if (auto && availableModels.length === 0) {
      logger.info('Skipping startup task-profile model config check: no models available yet.');
    } else {
      logger.info('Task-profile model config already matches the connected API well enough.');
    }
    return;
  }

  logger.warn(`[Config advisor] ${suggestion.summary}`);
  for (const line of suggestion.details) {
    logger.warn(`[Config advisor] ${line}`);
  }

  outputChannel.show(true);

  const choice = await vscode.window.showWarningMessage(
    suggestion.summary,
    { modal: true, detail: suggestion.details.join('\n') },
    'Save suggested config',
    'Keep current config'
  );

  if (auto) {
    await extensionContext?.globalState.update(GLOBAL_STATE_TASK_TYPE_CONFIG_PROMPTED, true);
  }

  if (choice !== 'Save suggested config') {
    logger.info('Kept the current task-profile model config.');
    return;
  }

  try {
    await vscode.workspace.getConfiguration(CONFIG_SECTION).update(
      'taskTypeModels',
      suggestion.updatedTaskTypeModels,
      vscode.ConfigurationTarget.Global,
    );
    vscode.window.showInformationMessage('Saved the suggested task-profile model config.');
    logger.info('Saved the suggested task-profile model config.');
  } catch (error) {
    logger.error(`Failed to save suggested task-profile model config: ${error}`);
    vscode.window.showErrorMessage(`Failed to save the suggested task-profile model config: ${error}`);
  }
}

export async function activate(context: vscode.ExtensionContext) {
  extensionContext = context;

  // Create output channel for logging
  outputChannel = vscode.window.createOutputChannel('LM Studio Provider');
  context.subscriptions.push(outputChannel);

  const logger = new Logger(outputChannel);

  logger.info(`LM Studio Copilot Provider is activating... v${context.extension.packageJSON.version}`);
  if (logger.shouldShow) {
    outputChannel.show(true);
  }

  if (vscode.workspace.getConfiguration(CONFIG_SECTION).get<boolean>('autoConfigureUtilityModel', true)) {
    try {
      if (await ensureByokUtilityModelDefault()) {
        logger.info(
          "Set chat.byokUtilityModelDefault to 'mainAgent' so Copilot's background utility tasks " +
          "(chat titles, commit messages, etc.) use this BYOK model instead of failing with " +
          "\"No utility model is configured for 'copilot-utility-small'\"."
        );
      }
    } catch (error) {
      logger.warn(`Could not auto-configure chat.byokUtilityModelDefault: ${error}`);
    }
  }

  // Spawn the C# backend process that owns the LM Studio HTTP/SSE client, task-profile
  // scoring/advisories, tool budgeting, and all tool implementations. See backend/README
  // (Program.cs) for the NDJSON stdio protocol this talks over.
  const backendDllPath = path.join(context.extensionPath, 'dist', 'backend', 'LmStudioBackend.dll');
  const workspaceCwd = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath ?? context.extensionPath;
  backend = new BackendProcess(backendDllPath, workspaceCwd, logger);
  try {
    await backend.start();
    await backend.configure(buildBackendConfig());
  } catch (error) {
    logger.error(`❌ Failed to start LM Studio backend process: ${error}`);
    vscode.window.showErrorMessage(
      `Failed to start the LM Studio backend process. Make sure the .NET 8 runtime is installed. (${error})`
    );
  }

  provider = new LMStudioProvider(backend, logger);

  // Register the provider with VS Code
  try {
    registration = vscode.lm.registerLanguageModelChatProvider(PROVIDER_VENDOR_ID, provider);
    context.subscriptions.push(registration);
    logger.info(`✅ Provider registered successfully with vendor: ${PROVIDER_VENDOR_ID}`);
  } catch (error) {
    logger.error(`❌ Failed to register provider: ${error}`);
    vscode.window.showErrorMessage(`Failed to register LM Studio provider: ${error}`);
  }

  // Register all LM tools (terminal, read_file, write_file, list_directory, search_files)
  registerAllTools(context, backend, logger);

  // Register commands
  context.subscriptions.push(
    vscode.commands.registerCommand(COMMAND_REFRESH_MODELS, async () => {
      logger.info('Refreshing models...');
      await provider?.refreshModels();
      vscode.window.showInformationMessage('LM Studio models refreshed');
      await reviewSuggestedModelConfig(false, logger);
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand(COMMAND_REVIEW_SUGGESTED_CONFIG, async () => {
      logger.info('Reviewing suggested model config...');
      await reviewSuggestedModelConfig(true, logger);
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand(COMMAND_CHECK_CONNECTION, async () => {
      const connected = (await backend?.checkConnection()) ?? false;
      if (connected) {
        vscode.window.showInformationMessage('✅ Connected to LM Studio server');
        logger.info('✅ Connection check: OK');
      } else {
        vscode.window.showErrorMessage('❌ Cannot connect to LM Studio server. Make sure it is running.');
        logger.error('❌ Connection check: FAILED');
      }
    })
  );

  // Listen for configuration changes
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration(async (e) => {
      if (e.affectsConfiguration(CONFIG_SECTION)) {
        logger.info('Configuration changed, refreshing models...');
        await backend?.configure(buildBackendConfig());
        await provider?.refreshModels();
        // Covers settings like apiKey/serverUrl being filled in after activation,
        // when the startup check ran too early to see any models. Still gated by
        // the same "already prompted" flag, so this won't re-nag after the first
        // time it's answered (e.g. from the 'Save suggested config' write below).
        await reviewSuggestedModelConfig(false, logger, true);
      }
    })
  );

  // Auto-refresh models on startup if enabled
  const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
  if (config.get<boolean>('autoRefreshModels', true)) {
    logger.info('Auto-refresh enabled, refreshing models now...');
    await provider?.refreshModels();
  }

  // Verify per-task models are configured; if not (or the model list changed
  // enough to make the current picks a poor fit), suggest a config. Only
  // shown once ever — see reviewSuggestedModelConfig's `auto` handling.
  await reviewSuggestedModelConfig(false, logger, true);

  logger.info(`LM Studio Copilot Provider activated v${context.extension.packageJSON.version}`);
}

export function deactivate() {
  provider?.dispose();
  provider = undefined;
  backend?.dispose();
  backend = undefined;
}
