import * as cp from 'child_process';
import * as readline from 'readline';
import { Logger } from './logger';
import {
  AdvertisedModel,
  BackendConfig,
  ChatMessage,
  MissingTaskTypeModel,
  StreamPart,
  TaskTypeModelConfigSuggestion,
  ToolSchema,
} from './types';

interface ChatStreamRequest {
  selectedId: string;
  messages: ChatMessage[];
  tools?: ToolSchema[];
  modelOptions?: { temperature?: number; maxOutputTokens?: number };
}

interface InvokeToolRequest {
  name: string;
  input: Record<string, unknown>;
  workspaceRoot: string;
  tokenBudget?: number;
}

type PendingResult = { resolve: (value: unknown) => void; reject: (error: Error) => void };
type PendingStream = { onChunk: (part: StreamPart) => void; onEnd: () => void; onError: (error: Error) => void };

/**
 * Spawns the C# backend process (backend/LmStudioBackend.csproj, published to
 * dist/backend/LmStudioBackend.dll) and speaks the NDJSON stdio protocol described in
 * backend/Protocol.cs: one JSON object per line, correlated by request id, with streaming
 * chatStream calls emitting "chunk"/"end"/"error" events and one-way "log" events.
 */
export class BackendProcess {
  private child: cp.ChildProcessWithoutNullStreams | undefined;
  private nextId = 0;
  private pendingResults = new Map<string, PendingResult>();
  private pendingStreams = new Map<string, PendingStream>();
  private startPromise: Promise<void> | undefined;

  constructor(
    private readonly dllPath: string,
    private readonly cwd: string,
    private readonly logger: Logger,
  ) {}

  private log(msg: string): void {
    this.logger.verbose(`[Backend] ${msg}`);
  }

  async start(): Promise<void> {
    if (this.startPromise) return this.startPromise;
    this.startPromise = this.doStart();
    return this.startPromise;
  }

  private async doStart(): Promise<void> {
    this.log(`Spawning: dotnet ${this.dllPath}`);
    const child = cp.spawn('dotnet', [this.dllPath], { cwd: this.cwd });
    this.child = child;

    const rl = readline.createInterface({ input: child.stdout });
    rl.on('line', (line) => this.handleLine(line));

    child.stderr.on('data', (data: Buffer) => {
      this.logger.error(`[Backend:stderr] ${data.toString().trimEnd()}`);
    });

    child.on('exit', (code, signal) => {
      this.logger.error(`[Backend] process exited (code=${code}, signal=${signal})`);
      const error = new Error(`LM Studio backend process exited unexpectedly (code=${code}, signal=${signal}). Make sure the .NET 8 runtime is installed and reload the window to restart it.`);
      for (const pending of this.pendingResults.values()) pending.reject(error);
      for (const pending of this.pendingStreams.values()) pending.onError(error);
      this.pendingResults.clear();
      this.pendingStreams.clear();
      this.child = undefined;
    });

    child.on('error', (error) => {
      this.logger.error(`[Backend] failed to start: ${error.message}`);
    });
  }

  private handleLine(line: string): void {
    if (!line.trim()) return;
    let msg: Record<string, unknown>;
    try {
      msg = JSON.parse(line);
    } catch (e) {
      this.logger.error(`[Backend] Failed to parse output line: ${line.slice(0, 300)}`);
      return;
    }

    if (msg.event === 'log') {
      const level = String(msg.level ?? 'info');
      const message = String(msg.message ?? '');
      if (level === 'error') this.logger.error(message);
      else if (level === 'warning') this.logger.warn(message);
      else if (level === 'info') this.logger.info(message);
      else this.logger.verbose(message);
      return;
    }

    const id = String(msg.id ?? '');
    if (!id) return;

    if (msg.event === 'chunk') {
      this.pendingStreams.get(id)?.onChunk(msg.part as StreamPart);
      return;
    }
    if (msg.event === 'end') {
      const stream = this.pendingStreams.get(id);
      this.pendingStreams.delete(id);
      stream?.onEnd();
      return;
    }
    if (msg.event === 'error') {
      const stream = this.pendingStreams.get(id);
      this.pendingStreams.delete(id);
      stream?.onError(new Error(String(msg.message ?? 'Unknown backend streaming error')));
      return;
    }

    const pending = this.pendingResults.get(id);
    if (!pending) return;
    this.pendingResults.delete(id);
    if (msg.ok) {
      pending.resolve(msg.result);
    } else {
      pending.reject(new Error(String(msg.error ?? 'Unknown backend error')));
    }
  }

  private send(id: string, method: string, params?: unknown): void {
    if (!this.child) {
      throw new Error('LM Studio backend process is not running.');
    }
    this.child.stdin.write(`${JSON.stringify({ id, method, params })}\n`);
  }

  private request<T>(method: string, params?: unknown): Promise<T> {
    const id = `req-${++this.nextId}`;
    return new Promise<T>((resolve, reject) => {
      this.pendingResults.set(id, { resolve: (v) => resolve(v as T), reject });
      try {
        this.send(id, method, params);
      } catch (e) {
        this.pendingResults.delete(id);
        reject(e as Error);
      }
    });
  }

  async configure(config: BackendConfig): Promise<void> {
    await this.request<void>('configure', config);
  }

  async checkConnection(): Promise<boolean> {
    return this.request<boolean>('checkConnection');
  }

  async refreshModels(): Promise<AdvertisedModel[]> {
    return this.request<AdvertisedModel[]>('refreshModels');
  }

  async getAdvertisedModels(): Promise<AdvertisedModel[]> {
    return this.request<AdvertisedModel[]>('getAdvertisedModels');
  }

  async getTaskTypeModelConfigSuggestion(): Promise<TaskTypeModelConfigSuggestion | null> {
    return this.request<TaskTypeModelConfigSuggestion | null>('getTaskTypeModelConfigSuggestion');
  }

  async getMissingTaskTypeModels(): Promise<MissingTaskTypeModel[]> {
    return this.request<MissingTaskTypeModel[]>('getMissingTaskTypeModels');
  }

  async invokeTool(req: InvokeToolRequest): Promise<string> {
    return this.request<string>('invokeTool', req);
  }

  /** Cancel an in-flight chatStream call by the same request id that was used to start it. */
  cancel(requestId: string): void {
    try {
      this.send(requestId, 'cancel');
    } catch {
      // Backend already gone — nothing to cancel.
    }
  }

  /** Streams a chat completion. The returned id doubles as the cancellation handle for cancel(). */
  chatStream(req: ChatStreamRequest): { id: string; stream: AsyncGenerator<StreamPart, void, unknown> } {
    const id = `chat-${++this.nextId}`;
    const queue: StreamPart[] = [];
    let ended = false;
    let error: Error | undefined;
    let wake: (() => void) | undefined;

    this.pendingStreams.set(id, {
      onChunk: (part) => { queue.push(part); wake?.(); },
      onEnd: () => { ended = true; wake?.(); },
      onError: (e) => { error = e; ended = true; wake?.(); },
    });

    try {
      this.send(id, 'chatStream', req);
    } catch (e) {
      this.pendingStreams.delete(id);
      throw e;
    }

    async function* generate(): AsyncGenerator<StreamPart, void, unknown> {
      while (true) {
        if (queue.length > 0) {
          yield queue.shift()!;
          continue;
        }
        if (ended) {
          if (error) throw error;
          return;
        }
        await new Promise<void>((resolve) => { wake = resolve; });
      }
    }

    return { id, stream: generate() };
  }

  dispose(): void {
    this.child?.kill();
    this.child = undefined;
  }
}
