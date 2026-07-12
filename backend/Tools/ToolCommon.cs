namespace LmStudioBackend.Tools;

internal static class ToolCommon
{
    public static string Truncate(string text, int maxChars, double headFraction = 0.55, double tailFraction = 0.35)
    {
        if (text.Length <= maxChars) return text;
        var head = (int)(maxChars * headFraction);
        var tail = (int)(maxChars * tailFraction);
        return text[..head] + $"\n\n... [{text.Length - head - tail} chars omitted] ...\n\n" + text[^tail..];
    }

    public static string ResolvePath(string workspaceRoot, string inputPath) =>
        Path.IsPathRooted(inputPath) ? inputPath : Path.Combine(workspaceRoot, inputPath);

    public static readonly HashSet<string> IgnoredDirs = new(StringComparer.Ordinal)
    {
        "node_modules", ".git", "dist", "out", ".cache", "__pycache__", ".venv", "venv", ".next", "build", "coverage", ".nyc_output",
    };

    public static readonly HashSet<string> BinaryExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".woff", ".woff2", ".ttf", ".eot",
        ".pdf", ".zip", ".tar", ".gz", ".exe", ".dll", ".so", ".bin", ".map", ".lock", ".vsix", ".snap",
    };
}
