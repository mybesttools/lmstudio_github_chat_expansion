namespace LmStudioBackend;

public enum LogLevel
{
    Verbose = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    None = 4,
}

/// <summary>Level-gated logger that emits log events over the IPC stdout channel for the shell to write into its VS Code Output Channel. Mirrors src/logger.ts.</summary>
public sealed class Logger
{
    private readonly StdoutWriter _writer;
    public LogLevel Level { get; set; } = LogLevel.Verbose;

    public Logger(StdoutWriter writer)
    {
        _writer = writer;
    }

    private void Emit(LogLevel level, string levelName, string message)
    {
        if (level < Level) return;
        try
        {
            _writer.Log(levelName, message);
        }
        catch
        {
            // Never let logging crash the backend.
        }
    }

    public void Verbose(string message) => Emit(LogLevel.Verbose, "verbose", message);
    public void Info(string message) => Emit(LogLevel.Info, "info", message);
    public void Warn(string message) => Emit(LogLevel.Warning, "warning", message);
    public void Error(string message) => Emit(LogLevel.Error, "error", message);

    public static LogLevel Parse(string? value) => value switch
    {
        "verbose" => LogLevel.Verbose,
        "info" => LogLevel.Info,
        "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "none" => LogLevel.None,
        _ => LogLevel.Verbose,
    };
}
