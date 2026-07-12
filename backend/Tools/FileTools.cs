using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LmStudioBackend.Tools;

public static class FileTools
{
    public const string ReadFileName = "mbt_lmstudio_read_file";
    public const string WriteFileName = "mbt_lmstudio_write_file";
    public const string ListDirectoryName = "mbt_lmstudio_list_directory";
    public const string SearchFilesName = "mbt_lmstudio_search_files";

    private static int BudgetChars(int? tokenBudget, int fallback) => tokenBudget.HasValue ? tokenBudget.Value * 3 : fallback;

    // ─── read_file ────────────────────────────────────────────────────────

    public static string ReadFile(string workspaceRoot, string path, int? startLine, int? endLine, int? tokenBudget)
    {
        var resolved = ToolCommon.ResolvePath(workspaceRoot, path);
        if (!File.Exists(resolved)) return $"File not found: {resolved}";

        string content;
        try { content = File.ReadAllText(resolved, Encoding.UTF8); }
        catch (Exception e) { return $"Error reading file: {e.Message}"; }

        var lines = content.Split('\n');
        var totalLines = lines.Length;
        var start = Math.Max(1, startLine ?? 1) - 1;
        var end = Math.Min(totalLines, endLine ?? totalLines);

        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            sb.Append((i + 1).ToString().PadLeft(6)).Append(" | ").Append(lines[i]);
            if (i < end - 1) sb.Append('\n');
        }

        var header = $"File: {resolved} (lines {start + 1}-{end} of {totalLines})\n\n";
        return ToolCommon.Truncate(header + sb, BudgetChars(tokenBudget, 16000));
    }

    // ─── write_file ───────────────────────────────────────────────────────

    public static string WriteFile(string workspaceRoot, string path, string content, bool createDirectories)
    {
        var resolved = ToolCommon.ResolvePath(workspaceRoot, path);
        try
        {
            if (createDirectories)
            {
                var dir = Path.GetDirectoryName(resolved);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            File.WriteAllText(resolved, content, new UTF8Encoding(false));
        }
        catch (Exception e) { return $"Error writing file: {e.Message}"; }

        var lineCount = content.Split('\n').Length;
        return $"Written: {resolved}\n{lineCount} lines, {Encoding.UTF8.GetByteCount(content)} bytes";
    }

    // ─── list_directory ───────────────────────────────────────────────────

    private static List<string> ListDir(string dirPath, int depth, int maxDepth, string prefix)
    {
        if (depth > maxDepth) return new List<string> { prefix + "  ... (max depth reached)" };

        IEnumerable<(string Name, bool IsDir)> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dirPath)
                .Select(p => (Name: Path.GetFileName(p), IsDir: Directory.Exists(p)));
        }
        catch
        {
            return new List<string> { prefix + "  [permission denied]" };
        }

        var sorted = entries
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        var lines = new List<string>();
        foreach (var (name, isDir) in sorted)
        {
            var icon = isDir ? "📁" : "📄";
            var suffix = isDir ? "/" : "";
            lines.Add($"{prefix}{icon} {name}{suffix}");

            if (isDir && !ToolCommon.IgnoredDirs.Contains(name))
            {
                lines.AddRange(ListDir(Path.Combine(dirPath, name), depth + 1, maxDepth, prefix + "  "));
            }
        }

        return lines;
    }

    public static string ListDirectory(string workspaceRoot, string? path, bool recursive, int? maxDepth, int? tokenBudget)
    {
        var dirPath = ToolCommon.ResolvePath(workspaceRoot, path ?? ".");
        if (!Directory.Exists(dirPath) && !File.Exists(dirPath)) return $"Directory not found: {dirPath}";
        if (!Directory.Exists(dirPath)) return $"Not a directory: {dirPath}";

        var effectiveMaxDepth = recursive ? (maxDepth ?? 4) : 1;
        var lines = ListDir(dirPath, 0, effectiveMaxDepth, "");
        var header = $"Directory: {dirPath}\n\n";
        var body = lines.Count > 0 ? string.Join('\n', lines) : "(empty directory)";
        return ToolCommon.Truncate(header + body, BudgetChars(tokenBudget, 16000));
    }

    // ─── search_files ─────────────────────────────────────────────────────

    private static bool HasCommand(string cmd)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "where" : "which", cmd)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<string> SearchWithRipgrepAsync(string pattern, string cwd, bool isRegex, bool caseSensitive, string? glob, int maxResults, int contextLines)
    {
        var args = new List<string> { "--color=never", "--with-filename", "--line-number" };
        if (!isRegex) args.Add("--fixed-strings");
        if (!caseSensitive) args.Add("--ignore-case");
        if (!string.IsNullOrEmpty(glob)) { args.Add("--glob"); args.Add(glob); }
        args.Add("--context"); args.Add(contextLines.ToString());
        args.Add("--max-count"); args.Add(maxResults.ToString());
        args.Add(pattern);

        var psi = new ProcessStartInfo("rg") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(15000);
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { /* best-effort */ } }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return !string.IsNullOrEmpty(stdout) ? stdout : (!string.IsNullOrEmpty(stderr) ? stderr : "No matches found.");
    }

    private static readonly string[] SkippedSearchDirs = { "node_modules", ".git", "dist", "out", "build", ".venv", "venv" };

    private static string SearchManually(string pattern, string cwd, bool isRegex, bool caseSensitive, int maxResults, int contextLines)
    {
        Regex regex;
        try
        {
            var source = isRegex ? pattern : Regex.Escape(pattern);
            regex = new Regex(source, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        }
        catch (Exception e)
        {
            return $"Invalid regex pattern: {e.Message}";
        }

        var results = new List<string>();
        var hitCount = 0;

        void Walk(string dir)
        {
            if (hitCount >= maxResults) return;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch { return; }

            foreach (var fullPath in entries)
            {
                if (hitCount >= maxResults) break;
                var name = Path.GetFileName(fullPath);

                if (Directory.Exists(fullPath))
                {
                    if (!SkippedSearchDirs.Contains(name)) Walk(fullPath);
                    continue;
                }

                var ext = Path.GetExtension(name);
                if (ToolCommon.BinaryExts.Contains(ext)) continue;

                string content;
                try { content = File.ReadAllText(fullPath); }
                catch { continue; }

                var lines = content.Split('\n');
                for (var i = 0; i < lines.Length && hitCount < maxResults; i++)
                {
                    if (!regex.IsMatch(lines[i])) continue;
                    var before = lines.Skip(Math.Max(0, i - contextLines)).Take(i - Math.Max(0, i - contextLines)).ToList();
                    var after = lines.Skip(i + 1).Take(Math.Min(lines.Length, i + 1 + contextLines) - (i + 1)).ToList();
                    var relPath = Path.GetRelativePath(cwd, fullPath);
                    var snippetLines = new List<string> { $"{relPath}:{i + 1}: {lines[i]}" };
                    for (var bi = 0; bi < before.Count; bi++) snippetLines.Add($"  {i - before.Count + bi + 1}: {before[bi]}");
                    for (var ai = 0; ai < after.Count; ai++) snippetLines.Add($"  {i + ai + 2}: {after[ai]}");
                    results.Add(string.Join('\n', snippetLines));
                    hitCount++;
                }
            }
        }

        Walk(cwd);
        return results.Count == 0 ? "No matches found." : string.Join("\n---\n", results);
    }

    public static async Task<string> SearchFilesAsync(string workspaceRoot, string pattern, string? glob, bool isRegex, bool caseSensitive, int? maxResults, int? contextLines, int? tokenBudget)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return "No search pattern provided.";

        var cwd = workspaceRoot;
        var effMaxResults = Math.Min(maxResults ?? 50, 200);
        var effContextLines = Math.Min(contextLines ?? 2, 10);

        var results = HasCommand("rg")
            ? await SearchWithRipgrepAsync(pattern, cwd, isRegex, caseSensitive, glob, effMaxResults, effContextLines)
            : SearchManually(pattern, cwd, isRegex, caseSensitive, effMaxResults, effContextLines);

        var header = $"Search results for \"{pattern}\" in {cwd}:\n\n";
        return ToolCommon.Truncate(header + results, BudgetChars(tokenBudget, 16000));
    }
}
