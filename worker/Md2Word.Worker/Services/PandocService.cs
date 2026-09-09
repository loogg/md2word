using System.Diagnostics;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal sealed class PandocService
{
    internal static IReadOnlyList<string> SemanticLuaFilterResourceNames { get; } = Array.AsReadOnly(
    [
        "shared-admonitions.lua",
        "worddom-schema-table-width.lua",
        "shared-heading-remap.lua",
    ]);

    internal static IReadOnlyList<string> LuaFilterResourceNames { get; } = Array.AsReadOnly(
    [
        .. SemanticLuaFilterResourceNames,
        "shared-figure-numbering.lua",
    ]);

    public async Task ConvertToHtmlAsync(
        string pandocPath,
        string sourcePath,
        string htmlOutputPath,
        int tocDepth,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pandocPath))
        {
            throw new WorkerCommandException("PANDOC_MISSING", "Pandoc path is required.", 3, "pandoc");
        }
        var templatePath = ConversionResourceLocator.Get("worddom-template.html");
        var luaFilterPaths = SemanticLuaFilterResourceNames
            .Select(name => ConversionResourceLocator.Get(Path.Combine("filters", name)))
            .ToArray();
        var sourceDirectory = Path.GetDirectoryName(sourcePath) ?? Directory.GetCurrentDirectory();
        var arguments = new List<string>
        {
            sourcePath,
            "--from=markdown",
            "--to=html5",
            "--standalone",
            $"--template={templatePath}",
        };
        arguments.AddRange(luaFilterPaths.Select(path => $"--lua-filter={path}"));
        arguments.AddRange(
        [
            "--wrap=none",
            $"--resource-path={sourceDirectory}",
            $"--metadata=toc-depth:{Math.Clamp(tocDepth, 1, 6)}",
            $"--output={htmlOutputPath}",
        ]);
        await RunPandocAsync(
            pandocPath,
            arguments,
            htmlOutputPath,
            "PANDOC_FAILED",
            "Pandoc failed to convert the Markdown",
            "pandoc",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyFigureNumberingAsync(
        string pandocPath,
        string htmlInputPath,
        string htmlOutputPath,
        bool figureCaptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pandocPath))
        {
            throw new WorkerCommandException("PANDOC_MISSING", "Pandoc path is required.", 3, "pandoc");
        }

        var templatePath = ConversionResourceLocator.Get("worddom-template.html");
        var figureFilterPath = ConversionResourceLocator.Get(Path.Combine("filters", "shared-figure-numbering.lua"));
        var arguments = new List<string>
        {
            htmlInputPath,
            "--from=html",
            "--to=html5",
            "--standalone",
            $"--template={templatePath}",
            $"--lua-filter={figureFilterPath}",
            "--wrap=none",
            $"--metadata=figure_captions:{figureCaptions.ToString().ToLowerInvariant()}",
            $"--output={htmlOutputPath}",
        };

        await RunPandocAsync(
            pandocPath,
            arguments,
            htmlOutputPath,
            "PANDOC_FIGURE_NUMBERING_FAILED",
            "Pandoc failed to finalize figure numbering",
            "pandoc",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunPandocAsync(
        string pandocPath,
        IReadOnlyList<string> arguments,
        string expectedOutputPath,
        string failureCode,
        string failureMessage,
        string stage,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pandocPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new WorkerCommandException("PANDOC_START_FAILED", "Pandoc could not be started.", 3, stage);
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new WorkerCommandException("PANDOC_MISSING", "Pandoc could not be started.", 3, stage, inner: exception);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        _ = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(expectedOutputPath))
        {
            throw new WorkerCommandException(failureCode, $"{failureMessage} (exit code {process.ExitCode}).", 4, stage);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process exited between the checks.
        }
    }
}
