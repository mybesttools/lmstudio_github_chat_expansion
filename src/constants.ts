import * as vscode from 'vscode';

export const CONFIG_SECTION = 'lmstudio-copilot-expansion';

export const PROVIDER_VENDOR_ID = 'lmstudio-mbt';

export const COMMAND_REFRESH_MODELS = 'lmstudio-copilot-expansion.refreshModels';
export const COMMAND_REVIEW_SUGGESTED_CONFIG = 'lmstudio-copilot-expansion.reviewSuggestedModelConfig';
export const COMMAND_CHECK_CONNECTION = 'lmstudio-copilot-expansion.checkConnection';

// globalState key: set once the automatic startup task-profile config prompt
// has been shown and answered, so it doesn't nag on every subsequent launch.
export const GLOBAL_STATE_TASK_TYPE_CONFIG_PROMPTED = 'lmstudio-copilot-expansion.taskTypeModelConfigPrompted';

type ConfigurationInspection<T> = {
  globalValue?: T;
  workspaceValue?: T;
  workspaceFolderValue?: T;
};

function hasExplicitValue<T>(inspection: ConfigurationInspection<T> | undefined): boolean {
  return Boolean(
    inspection?.globalValue !== undefined ||
      inspection?.workspaceValue !== undefined ||
      inspection?.workspaceFolderValue !== undefined
  );
}

const CHAT_CONFIG_SECTION = 'chat';
const BYOK_UTILITY_MODEL_DEFAULT_KEY = 'byokUtilityModelDefault';

/**
 * VS Code's built-in utility flows (chat titles, commit messages, intent
 * detection, etc.) need a "utility model". With a BYOK main agent model and
 * no GitHub Copilot session, there is none by default, which surfaces as
 * "No utility model is configured for 'copilot-utility-small' while the
 * selected main agent model is BYOK." Pointing `chat.byokUtilityModelDefault`
 * at `mainAgent` reuses whichever BYOK model is already selected, so it
 * stays correct as the user switches models instead of pinning a stale id.
 *
 * Only applies when the user hasn't explicitly set this setting themselves.
 * Returns true if the setting was changed.
 */
export async function ensureByokUtilityModelDefault(): Promise<boolean> {
  const chatConfig = vscode.workspace.getConfiguration(CHAT_CONFIG_SECTION);
  const inspected = chatConfig.inspect<string>(BYOK_UTILITY_MODEL_DEFAULT_KEY);

  if (hasExplicitValue(inspected)) {
    return false;
  }

  await chatConfig.update(BYOK_UTILITY_MODEL_DEFAULT_KEY, 'mainAgent', vscode.ConfigurationTarget.Global);
  return true;
}
