import * as vscode from 'vscode';
import { CONFIG_SECTION } from './constants';

export type LogLevel = 'verbose' | 'info' | 'warning' | 'error' | 'none';
const VALID_LOG_LEVELS: ReadonlySet<LogLevel> = new Set<LogLevel>([
  'verbose',
  'info',
  'warning',
  'error',
  'none',
]);

const LEVEL_ORDER: Record<LogLevel, number> = {
  verbose: 0,
  info: 1,
  warning: 2,
  error: 3,
  none: 4,
};

/**
 * Wraps a VS Code OutputChannel with level-gated logging.
 *
 * Reads `lmstudio-copilot-expansion.logLevel` from VS Code settings on every call,
 * so the user can change it at runtime without reloading the extension.
 */
export class Logger {
  constructor(private channel: vscode.OutputChannel) {}

  private get configuredLevel(): LogLevel {
    const configuredValue = vscode.workspace
      .getConfiguration(CONFIG_SECTION)
      .get<string>('logLevel', 'verbose');

    if (VALID_LOG_LEVELS.has(configuredValue as LogLevel)) {
      return configuredValue as LogLevel;
    }

    return 'verbose';
  }

  private shouldLog(level: LogLevel): boolean {
    return LEVEL_ORDER[level] >= LEVEL_ORDER[this.configuredLevel];
  }

  /** Detailed diagnostic output — suppressed at 'info' and above. */
  verbose(msg: string): void {
    if (this.shouldLog('verbose')) {
      this.channel.appendLine(msg);
    }
  }

  /** State changes, model discovery, important events. */
  info(msg: string): void {
    if (this.shouldLog('info')) {
      this.channel.appendLine(msg);
    }
  }

  /** Warnings about fallbacks, unavailability, etc. */
  warn(msg: string): void {
    if (this.shouldLog('warning')) {
      this.channel.appendLine(msg);
    }
  }

  /** Errors — always logged unless level is 'none'. */
  error(msg: string): void {
    if (this.shouldLog('error')) {
      this.channel.appendLine(msg);
    }
  }

  /** Whether the output channel should be revealed on startup. */
  get shouldShow(): boolean {
    return this.configuredLevel !== 'none';
  }
}
