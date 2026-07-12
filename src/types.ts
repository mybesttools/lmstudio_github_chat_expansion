/**
 * Wire-format types shared with the C# backend process (backend/*.cs). The shell converts
 * vscode.LanguageModelChatRequestMessage[]/LanguageModelChatTool[] into these plain-JSON shapes
 * before sending them over IPC; everything OpenAI-API-specific (HTTP calls, SSE parsing, model
 * load orchestration) lives entirely in the backend now.
 */

/** Content part for multi-modal messages */
export type ChatMessageContentPart =
  | { type: 'text'; text: string }
  | { type: 'image_url'; image_url: { url: string } };

/** Chat message sent to the backend for a chatStream request. */
export interface ChatMessage {
  role: 'system' | 'user' | 'assistant' | 'tool';
  content: string | ChatMessageContentPart[] | null;
  tool_call_id?: string;
  tool_calls?: ToolCall[];
}

/** A tool call, either sent as part of assistant history or received back from a stream chunk. */
export interface ToolCall {
  id: string;
  type: 'function';
  function: {
    name: string;
    arguments: string;
  };
}

/** Tool schema as VS Code advertises it (vscode.LanguageModelChatTool), before backend-side budgeting. */
export interface ToolSchema {
  name: string;
  description?: string;
  inputSchema?: Record<string, unknown>;
}

/** A model entry as advertised to VS Code's model picker; returned by the backend's getAdvertisedModels. */
export interface AdvertisedModel {
  id: string;
  name: string;
  family: string;
  version: string;
  maxInputTokens: number;
  maxOutputTokens: number;
  lmstudioModelId: string;
  isLoaded: boolean;
  isUserSelectable: boolean;
  toolCalling: boolean;
  imageInput: boolean;
  taskProfileKey?: string;
}

export interface TaskProfileChange {
  key: string;
  currentModelId: string;
  currentModelName: string;
  suggestedModelId: string;
  suggestedModelName: string;
  reason: string;
  isMissing: boolean;
}

export interface TaskTypeModelConfigSuggestion {
  summary: string;
  details: string[];
  updatedTaskTypeModels: Record<string, string>;
  changes: TaskProfileChange[];
  hasMissingModels: boolean;
}

/** A configured task-profile model that no longer resolves to any model on the connected server. */
export interface MissingTaskTypeModel {
  key: string;
  label: string;
  modelId: string;
}

/** Configuration snapshot sent to the backend on activation and on every settings change. */
export interface BackendConfig {
  serverUrl: string;
  apiKey: string;
  requestTimeoutMs: number;
  modelIdleTtl: number;
  maxInputTokens: number;
  maxOutputTokens: number;
  maxTools: number;
  enableToolCalling: boolean;
  injectSystemPrompt: boolean;
  enableThinking: boolean;
  reasoningEffort: string;
  enableModelAdvisories: boolean;
  enableAutoModel: boolean;
  taskTypeModels: Record<string, string>;
  enableTerminalTool: boolean;
  terminalToolName: string;
  terminalToolTimeoutMs: number;
  logLevel: string;
}

/** A streamed piece of a chat completion, as relayed by the backend's chatStream chunks. */
export type StreamPart =
  | { text: string }
  | { toolCall: ToolCall };
