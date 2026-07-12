using System.Diagnostics;
using System.Text;

namespace LmStudioBackend.Tools;

public static class TerminalTool
{
    public const string Name = "mbt_lmstudio_run_in_terminal";

    public sealed record ExecResult(string Stdout, string Stderr, int ExitCode);

    private static async Task<ExecResult> ExecAsync(string command, string cwd, int timeoutMs, CancellationToken ct)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c {command}")
            : new ProcessStartInfo("/bin/bash", $"-c \"{command.Replace("\"", "\\\"")}\"");

        psi.WorkingDirectory = cwd;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best-effort */ }
            return new ExecResult(stdout.ToString(), stderr.ToString(), 1);
        }

        return new ExecResult(stdout.ToString(), stderr.ToString(), process.ExitCode);
    }

    public static async Task<string> InvokeAsync(string command, string cwd, int timeoutMs, int? tokenBudget, CancellationToken ct)
    {
        command = command.Trim();
        if (command.Length == 0) return "No command provided.";

        var (stdout, stderr, exitCode) = await ExecAsync(command, cwd, timeoutMs, ct);

        var budgetChars = (tokenBudget ?? 4000) * 3;
        var parts = new List<string> { $"exit_code: {exitCode}" };

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            parts.Add($"stdout:\n{ToolCommon.Truncate(stdout.TrimEnd(), (int)(budgetChars * 0.7), 0.6, 0.3)}");
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            parts.Add($"stderr:\n{ToolCommon.Truncate(stderr.TrimEnd(), (int)(budgetChars * 0.25), 0.6, 0.3)}");
        }
        if (string.IsNullOrWhiteSpace(stdout) && string.IsNullOrWhiteSpace(stderr))
        {
            parts.Add("(no output)");
        }

        return string.Join("\n\n", parts);
    }
}
