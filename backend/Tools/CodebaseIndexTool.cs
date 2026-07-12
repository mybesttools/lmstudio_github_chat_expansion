using System.Text.RegularExpressions;

namespace LmStudioBackend.Tools;

public static class CodebaseIndexTool
{
    public const string Name = "mbt_lmstudio_get_codebase_index";

    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".rb", ".go", ".rs",
        ".java", ".cs", ".cpp", ".c", ".h", ".hpp", ".swift", ".kt", ".php", ".vue", ".svelte",
    };

    private sealed record FileEntry(string RelPath, long SizeBytes, List<string> Symbols);

    /// <summary>Extracts top-level declaration names via simple regex heuristics. Mirrors extractSymbols in codebase-index-tool.ts.</summary>
    private static List<string> ExtractSymbols(string content, string ext)
    {
        var symbols = new List<string>();
        var seen = new HashSet<string>();
        void Add(string name) { if (name.Length > 0 && seen.Add(name)) symbols.Add(name); }

        var patterns = new List<Regex>();
        var extLower = ext.ToLowerInvariant();

        if (new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs" }.Contains(extLower))
        {
            patterns.Add(new Regex(@"^\s*export\s+(?:async\s+)?function\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*export\s+(?:abstract\s+)?class\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*export\s+(?:const|let|var)\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*export\s+(?:type|interface|enum)\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*export\s+default\s+(?:async\s+)?function\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*export\s+default\s+class\s+(\w+)", RegexOptions.Multiline));
        }
        else if (extLower == ".py")
        {
            patterns.Add(new Regex(@"^\s*def\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*class\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*async\s+def\s+(\w+)", RegexOptions.Multiline));
        }
        else if (extLower == ".go")
        {
            patterns.Add(new Regex(@"^\s*func\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*type\s+(\w+)\s+(?:struct|interface)", RegexOptions.Multiline));
        }
        else if (extLower == ".rs")
        {
            patterns.Add(new Regex(@"^\s*pub\s+(?:async\s+)?fn\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*pub\s+struct\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*pub\s+enum\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*pub\s+trait\s+(\w+)", RegexOptions.Multiline));
        }
        else if (new[] { ".java", ".cs" }.Contains(extLower))
        {
            patterns.Add(new Regex(@"^\s*(?:public|protected|private|internal)\s+(?:static\s+)?(?:\w+\s+)*class\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*(?:public|protected|private|internal)\s+(?:static\s+)?(?:override\s+|virtual\s+|abstract\s+)?(?:void|int|long|bool|boolean|string|String|double|float|byte|short|char|\w+(?:<[^>]+>)?(?:\[\])?)\s+(\w+)\s*\(", RegexOptions.Multiline));
        }
        else if (extLower == ".rb")
        {
            patterns.Add(new Regex(@"^\s*def\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*class\s+(\w+)", RegexOptions.Multiline));
            patterns.Add(new Regex(@"^\s*module\s+(\w+)", RegexOptions.Multiline));
        }

        foreach (var regex in patterns)
        {
            foreach (Match m in regex.Matches(content)) Add(m.Groups[1].Value);
        }

        return symbols;
    }

    private static void CollectFiles(string dir, string rootDir, long maxFileSizeBytes, bool includeSymbols, List<FileEntry> results)
    {
        IEnumerable<(string Name, string FullPath, bool IsDir)> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir)
                .Select(p => (Name: Path.GetFileName(p), FullPath: p, IsDir: Directory.Exists(p)));
        }
        catch { return; }

        var sorted = entries.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.Ordinal);

        foreach (var (name, fullPath, isDir) in sorted)
        {
            var relPath = Path.GetRelativePath(rootDir, fullPath);

            if (isDir)
            {
                if (!ToolCommon.IgnoredDirs.Contains(name)) CollectFiles(fullPath, rootDir, maxFileSizeBytes, includeSymbols, results);
                continue;
            }

            var ext = Path.GetExtension(name);
            if (ToolCommon.BinaryExts.Contains(ext)) continue;

            long sizeBytes;
            try { sizeBytes = new FileInfo(fullPath).Length; }
            catch { continue; }

            var symbols = new List<string>();
            if (includeSymbols && CodeExts.Contains(ext) && sizeBytes <= maxFileSizeBytes)
            {
                try { symbols = ExtractSymbols(File.ReadAllText(fullPath), ext); }
                catch { /* best-effort */ }
            }

            results.Add(new FileEntry(relPath, sizeBytes, symbols));
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        return $"{bytes / (1024.0 * 1024.0):F1}MB";
    }

    public static string GetIndex(string workspaceRoot, bool includeSymbols, int? maxFileSizeKb, int? tokenBudget)
    {
        var requestedKb = maxFileSizeKb ?? 128;
        var cappedKb = Math.Min(requestedKb, 512);
        var maxFileSizeBytes = cappedKb * 1024L;

        var files = new List<FileEntry>();
        CollectFiles(workspaceRoot, workspaceRoot, maxFileSizeBytes, includeSymbols, files);

        if (files.Count == 0) return "No files found in workspace.";

        var lines = new List<string> { $"Codebase Index: {workspaceRoot}", $"Files indexed: {files.Count}", "" };
        foreach (var file in files)
        {
            var sizeLabel = FormatSize(file.SizeBytes);
            lines.Add($"{file.RelPath} ({sizeLabel})");
            if (file.Symbols.Count > 0) lines.Add($"  symbols: {string.Join(", ", file.Symbols)}");
        }

        var full = string.Join('\n', lines);
        var budget = tokenBudget.HasValue ? tokenBudget.Value * 3 : 24000;
        return ToolCommon.Truncate(full, budget);
    }
}
