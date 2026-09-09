using System.Diagnostics;
using System.Reflection;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal sealed class DiagnosticsService
{
    private readonly Func<string, string, ToolProbeResult> _toolProbe;
    private readonly Func<MermaidBrowserProbeResult> _mermaidBrowserProbe;

    public DiagnosticsService()
        : this(ProbeTool, ProbeMermaidBrowser)
    {
    }

    internal DiagnosticsService(
        Func<string, string, ToolProbeResult> toolProbe,
        Func<MermaidBrowserProbeResult> mermaidBrowserProbe)
    {
        _toolProbe = toolProbe;
        _mermaidBrowserProbe = mermaidBrowserProbe;
    }

    public EnvironmentStatus Diagnose()
    {
        var items = new List<EnvironmentItem>();
        var windowsReady = OperatingSystem.IsWindows();
        items.Add(new EnvironmentItem(
            "windows",
            "Windows",
            windowsReady ? "ready" : "blocked",
            true,
            Environment.OSVersion.VersionString,
            windowsReady ? "Windows desktop runtime is available." : "MD2Word Worker only supports Windows."));

        var wordType = windowsReady ? Type.GetTypeFromProgID("Word.Application", throwOnError: false) : null;
        items.Add(new EnvironmentItem(
            "word",
            "Microsoft Word",
            wordType is not null ? "ready" : "blocked",
            true,
            wordType is not null ? "Word.Application" : "unavailable",
            wordType is not null ? "Word COM registration is present." : "Word.Application COM registration was not found."));

        var pandoc = _toolProbe("pandoc", "--version");
        items.Add(new EnvironmentItem(
            "pandoc",
            "Pandoc",
            pandoc.Available ? "ready" : "blocked",
            true,
            pandoc.Version,
            pandoc.Available ? "Pandoc is available on PATH." : "Pandoc was not found on PATH."));

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "development";
        items.Add(new EnvironmentItem("worker", "C# Word Worker", "ready", true, version, ".NET 8 Worker is running."));

        var npx = _toolProbe("npx", "--version");
        var browser = _mermaidBrowserProbe();
        var mermaidReady = npx.Available && browser.Available;
        items.Add(new EnvironmentItem(
            "mermaid",
            "Mermaid",
            mermaidReady ? "ready" : "optional-missing",
            false,
            npx.Available ? $"npx {npx.Version}" : npx.Version,
            BuildMermaidDetail(npx.Available, browser)));

        var overall = items.Any(item => item.Required && item.Status != "ready")
            ? "blocked"
            : items.Any(item => item.Status == "optional-missing") ? "degraded" : "ready";
        return new EnvironmentStatus(DateTimeOffset.UtcNow.ToString("O"), overall, items);
    }

    private static string BuildMermaidDetail(bool npxAvailable, MermaidBrowserProbeResult browser)
    {
        if (npxAvailable && browser.Available)
        {
            return $"npx and {browser.ProductName} are available; Mermaid is resolved only when requested.";
        }

        if (!npxAvailable && !browser.Available)
        {
            return "npx and a supported Microsoft Edge or Google Chrome browser are unavailable; Mermaid auto mode will be degraded.";
        }

        return npxAvailable
            ? "A supported Microsoft Edge or Google Chrome browser was not found; Mermaid auto mode will be degraded."
            : "npx is unavailable; Mermaid auto mode will be degraded.";
    }

    private static MermaidBrowserProbeResult ProbeMermaidBrowser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return MermaidBrowserProbeResult.Unavailable;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new (string Root, string RelativePath, string ProductName)[]
        {
            (programFilesX86, Path.Combine("Microsoft", "Edge", "Application", "msedge.exe"), "Microsoft Edge"),
            (programFiles, Path.Combine("Microsoft", "Edge", "Application", "msedge.exe"), "Microsoft Edge"),
            (localApplicationData, Path.Combine("Microsoft", "Edge", "Application", "msedge.exe"), "Microsoft Edge"),
            (programFiles, Path.Combine("Google", "Chrome", "Application", "chrome.exe"), "Google Chrome"),
            (programFilesX86, Path.Combine("Google", "Chrome", "Application", "chrome.exe"), "Google Chrome"),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Root))
            {
                continue;
            }

            var executablePath = Path.Combine(candidate.Root, candidate.RelativePath);
            if (File.Exists(executablePath))
            {
                return new MermaidBrowserProbeResult(true, candidate.ProductName, executablePath);
            }
        }

        foreach (var executable in new[] { "msedge.exe", "chrome.exe" })
        {
            var executablePath = ResolveExecutableOnPath(executable);
            if (executablePath is not null)
            {
                return new MermaidBrowserProbeResult(
                    true,
                    executable.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" : "Google Chrome",
                    executablePath);
            }
        }

        return MermaidBrowserProbeResult.Unavailable;
    }

    private static string? ResolveExecutableOnPath(string executable)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore malformed PATH entries and continue with the remaining candidates.
            }
        }

        return null;
    }

    private static ToolProbeResult ProbeTool(string executable, string versionArgument)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add(versionArgument);
            if (!process.Start())
            {
                return ToolProbeResult.Unavailable;
            }
            var firstLine = process.StandardOutput.ReadLine();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return new ToolProbeResult(false, "timeout");
            }
            return process.ExitCode == 0
                ? new ToolProbeResult(true, string.IsNullOrWhiteSpace(firstLine) ? "available" : firstLine.Trim())
                : ToolProbeResult.Unavailable;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ToolProbeResult.Unavailable;
        }
    }
}

internal readonly record struct ToolProbeResult(bool Available, string Version)
{
    public static ToolProbeResult Unavailable { get; } = new(false, "unavailable");
}

internal sealed record MermaidBrowserProbeResult(bool Available, string ProductName, string? ExecutablePath)
{
    public static MermaidBrowserProbeResult Unavailable { get; } = new(false, "unavailable", null);
}
