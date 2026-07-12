import * as vscode from 'vscode';
import { BackendProcess } from './backend-process';
import { Logger } from './logger';
import { AdvertisedModel, ChatMessage, ChatMessageContentPart, MissingTaskTypeModel, ToolSchema, TaskTypeModelConfigSuggestion } from './types';

interface LMStudioModelInfo extends vscode.LanguageModelChatInformation {
  lmstudioModelId: string;
  isUserSelectable: boolean;
}

/**
 * VS Code-facing shell for the LM Studio chat provider. All business logic (model advertising,
 * task-profile scoring/advisories, tool budgeting, message normalization, HTTP/SSE handling)
 * lives in the C# backend (backend/ChatOrchestrator.cs, backend/LmStudioClient.cs) — this class's
 * only job is translating between vscode.LanguageModelChatProvider's types and the backend's
 * plain-JSON wire format, since that translation inherently touches vscode-only classes
 * (LanguageModelTextPart, ToolCallPart, ToolResultPart, DataPart) that can't cross the IPC boundary.
 */
export class LMStudioProvider implements vscode.LanguageModelChatProvider<LMStudioModelInfo> {
  private _onDidChangeLanguageModelChatInformation = new vscode.EventEmitter<void>();
  readonly onDidChangeLanguageModelChatInformation = this._onDidChangeLanguageModelChatInformation.event;
  private disposables: vscode.Disposable[] = [];

  constructor(
    private backend: BackendProcess,
    private logger: Logger
  ) {
    this.disposables.push(this._onDidChangeLanguageModelChatInformation);
  }

  private log(msg: string): void {
    this.logger.verbose(`[Provider] ${msg}`);
  }

  async refreshModels(): Promise<void> {
    this.log('Refreshing models...');
    await this.backend.refreshModels();
    this._onDidChangeLanguageModelChatInformation.fire();
  }

  async getTaskTypeModelConfigSuggestion(): Promise<TaskTypeModelConfigSuggestion | null> {
    return this.backend.getTaskTypeModelConfigSuggestion();
  }

  async getMissingConfiguredModels(): Promise<MissingTaskTypeModel[]> {
    return this.backend.getMissingTaskTypeModels();
  }

  async getAvailableModels(): Promise<AdvertisedModel[]> {
    return this.backend.getAdvertisedModels();
  }

  private toModelInfo(m: AdvertisedModel): LMStudioModelInfo {
    return {
      id: m.id,
      name: m.name,
      family: m.family,
      version: m.version,
      maxInputTokens: m.maxInputTokens,
      maxOutputTokens: m.maxOutputTokens,
      lmstudioModelId: m.lmstudioModelId,
      isUserSelectable: m.isUserSelectable,
      capabilities: {
        toolCalling: m.toolCalling,
        imageInput: m.imageInput,
      },
    };
  }

  async provideLanguageModelChatInformation(
    _options: vscode.PrepareLanguageModelChatModelOptions,
    _token: vscode.CancellationToken
  ): Promise<LMStudioModelInfo[]> {
    const advertised = await this.backend.getAdvertisedModels();
    this.log(`provideLanguageModelChatInformation: ${advertised.length} advertised models`);
    return advertised.map((m) => this.toModelInfo(m));
  }

  // ──────────────────────────────────────────────────────────────────────
  // Chat response — relay only; all orchestration happens in the backend.
  // ──────────────────────────────────────────────────────────────────────

  async provideLanguageModelChatResponse(
    model: LMStudioModelInfo,
    messages: readonly vscode.LanguageModelChatRequestMessage[],
    options: vscode.ProvideLanguageModelChatResponseOptions,
    progress: vscode.Progress<vscode.LanguageModelResponsePart>,
    token: vscode.CancellationToken
  ): Promise<void> {
    const wireMessages = this.convertMessages(messages);
    const wireTools = this.convertToolSchemas(options.tools);

    const { id, stream } = this.backend.chatStream({
      selectedId: model.id,
      messages: wireMessages,
      tools: wireTools,
      modelOptions: {
        temperature: options.modelOptions?.temperature as number | undefined,
        maxOutputTokens: options.modelOptions?.maxOutputTokens as number | undefined,
      },
    });

    token.onCancellationRequested(() => this.backend.cancel(id));

    for await (const part of stream) {
      if (token.isCancellationRequested) break;

      if ('text' in part) {
        progress.report(new vscode.LanguageModelTextPart(part.text));
      } else if ('toolCall' in part) {
        const tc = part.toolCall;
        let parsedArgs: Record<string, unknown>;
        try {
          parsedArgs = JSON.parse(tc.function.arguments || '{}');
        } catch {
          this.logger.warn(`[Provider] Tool call "${tc.function.name}" has invalid JSON args: ${tc.function.arguments}`);
          parsedArgs = {};
        }
        progress.report(new vscode.LanguageModelToolCallPart(tc.id, tc.function.name, parsedArgs));
      }
    }
  }

  // ──────────────────────────────────────────────────────────────────────
  // Token counting — cheap enough to keep local rather than round-trip.
  // ──────────────────────────────────────────────────────────────────────

  async provideTokenCount(
    _model: LMStudioModelInfo,
    text: string | vscode.LanguageModelChatRequestMessage,
    _token: vscode.CancellationToken
  ): Promise<number> {
    if (typeof text === 'string') {
      return Math.ceil(text.length / 4);
    }

    const converted = this.convertToChatMessageContent(text.content);
    let length = 0;
    if (typeof converted === 'string') {
      length = converted.length;
    } else if (Array.isArray(converted)) {
      for (const part of converted) {
        if (part.type === 'text') {
          length += part.text.length;
        } else if (part.type === 'image_url') {
          length += 4000;
        }
      }
    }
    return Math.ceil(length / 4);
  }

  // ──────────────────────────────────────────────────────────────────────
  // Message conversion: VS Code → wire format
  // ──────────────────────────────────────────────────────────────────────

  private convertMessages(messages: readonly vscode.LanguageModelChatRequestMessage[]): ChatMessage[] {
    const result: ChatMessage[] = [];

    for (const msg of messages) {
      const parts = Array.isArray(msg.content) ? msg.content : [];

      const toolResultParts = parts.filter(
        (p): p is vscode.LanguageModelToolResultPart => p instanceof vscode.LanguageModelToolResultPart
      );
      if (toolResultParts.length > 0) {
        for (const part of toolResultParts) {
          result.push({
            role: 'tool',
            content: this.extractToolResultText(part.content),
            tool_call_id: part.callId,
          });
        }
        const userContent = this.convertToChatMessageContent(msg.content);
        if (!this.isContentEmpty(userContent)) {
          result.push({ role: 'user', content: userContent });
        }
        continue;
      }

      if (msg.role === vscode.LanguageModelChatMessageRole.Assistant) {
        const toolCallParts = parts.filter(
          (p): p is vscode.LanguageModelToolCallPart => p instanceof vscode.LanguageModelToolCallPart
        );
        if (toolCallParts.length > 0) {
          const text = parts
            .filter((p): p is vscode.LanguageModelTextPart => p instanceof vscode.LanguageModelTextPart)
            .map((p) => p.value)
            .join('');

          result.push({
            role: 'assistant',
            content: text || null,
            tool_calls: toolCallParts.map((tc) => ({
              id: tc.callId,
              type: 'function' as const,
              function: {
                name: tc.name,
                arguments: typeof tc.input === 'string' ? tc.input : JSON.stringify(tc.input),
              },
            })),
          });
          continue;
        }
      }

      const convertedContent = this.convertToChatMessageContent(msg.content);
      if (!this.isContentEmpty(convertedContent)) {
        result.push({
          role: this.mapRole(msg.role),
          content: convertedContent,
        });
      }
    }

    return result;
  }

  private convertToChatMessageContent(
    content: string | readonly unknown[]
  ): string | ChatMessageContentPart[] {
    if (typeof content === 'string') return content;

    const parts: ChatMessageContentPart[] = [];
    for (const part of content) {
      if (part instanceof vscode.LanguageModelTextPart) {
        parts.push({ type: 'text', text: part.value });
      } else if (part instanceof vscode.LanguageModelDataPart && part.mimeType.startsWith('image/')) {
        const base64 = Buffer.from(part.data).toString('base64');
        parts.push({
          type: 'image_url',
          image_url: { url: `data:${part.mimeType};base64,${base64}` },
        });
      }
    }

    if (parts.length === 1 && parts[0].type === 'text') {
      return parts[0].text;
    }

    return parts;
  }

  private isContentEmpty(content: string | ChatMessageContentPart[] | null): boolean {
    if (!content) return true;
    if (typeof content === 'string') return content.trim().length === 0;
    if (Array.isArray(content)) {
      if (content.length === 0) return true;
      return content.every((part) => part.type === 'text' && part.text.trim().length === 0);
    }
    return false;
  }

  private mapRole(role: vscode.LanguageModelChatMessageRole): 'system' | 'user' | 'assistant' {
    switch (role) {
      case vscode.LanguageModelChatMessageRole.User: return 'user';
      case vscode.LanguageModelChatMessageRole.Assistant: return 'assistant';
      default: return 'user';
    }
  }

  private extractToolResultText(content: unknown): string {
    if (typeof content === 'string') return content;
    if (Array.isArray(content)) {
      return content
        .map((c) => {
          if (c instanceof vscode.LanguageModelTextPart) return c.value;
          if (typeof c === 'object' && c !== null && 'value' in c) return String((c as { value: unknown }).value);
          return typeof c === 'string' ? c : JSON.stringify(c);
        })
        .join('');
    }
    return String(content);
  }

  private convertToolSchemas(tools: readonly vscode.LanguageModelChatTool[] | undefined): ToolSchema[] | undefined {
    if (!tools?.length) return undefined;
    return tools.map((t) => ({
      name: t.name,
      description: t.description,
      inputSchema: t.inputSchema as Record<string, unknown> | undefined,
    }));
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
    this.disposables = [];
  }
}
