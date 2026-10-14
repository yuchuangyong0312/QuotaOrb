using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuotaOrb.Services;

public sealed class CodexInstallation
{
    public const string MissingMessage = "未找到已安装的 Codex。请先安装 Codex，再点击“立即核验”。";
    private readonly string[] desktopRoots;
    private readonly Func<string?> searchPath;

    public CodexInstallation() : this(
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin")],
        () => Environment.GetEnvironmentVariable("PATH")) { }

    internal CodexInstallation(IEnumerable<string> desktopRoots, Func<string?> searchPath)
    {
        this.desktopRoots = desktopRoots.ToArray();
        this.searchPath = searchPath;
    }

    // Discover each time: Codex updates replace the version directory, and it may be installed after this app starts.
    public string? FindExecutable()
    {
        foreach (var root in desktopRoots)
        {
            var candidates = new List<(string Path, DateTime Modified)>();
            try
            {
                AddCandidate(root);
                foreach (var directory in Directory.GetDirectories(root)) AddCandidate(directory);
            }
            catch (Exception error) when (IsPathError(error)) { }
            foreach (var candidate in candidates.OrderByDescending(c => c.Modified))
                if (File.Exists(candidate.Path)) return candidate.Path;

            void AddCandidate(string directory)
            {
                try
                {
                    var executable = Path.GetFullPath(Path.Combine(directory, "codex.exe"));
                    if (File.Exists(executable)) candidates.Add((executable, File.GetLastWriteTimeUtc(executable)));
                }
                catch (Exception error) when (IsPathError(error)) { }
            }
        }

        // Also support an installed CLI exposed directly on PATH. Never fall back to an app-bundled runtime.
        foreach (var directory in (searchPath() ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(expanded)) continue;
                var executable = Path.Combine(expanded, "codex.exe");
                if (File.Exists(executable)) return Path.GetFullPath(executable);
            }
            catch (Exception error) when (IsPathError(error)) { }
        }
        return null;
    }

    public string RequireExecutable() => FindExecutable() ?? throw new FileNotFoundException(MissingMessage);

    private static bool IsPathError(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
