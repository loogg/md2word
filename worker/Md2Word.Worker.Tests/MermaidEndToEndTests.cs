using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class MermaidEndToEndTests
{
    [MermaidE2EFact]
    public async Task PinnedCliRendersSyntheticSequenceDiagramAndNumbersCaption()
    {
        using var workspace = new SyntheticWorkspace();
        var sourceHtml = """
            <!doctype html><html><body>
            <h1>Synthetic chapter</h1>
            <pre class="mermaid"><code>sequenceDiagram
              participant A as Device
              participant B as Network
              A-&gt;&gt;B: Discover
              B--&gt;&gt;A: Configure</code></pre>
            <!-- caption: 设备发现与网络配置系统架构时序 -->
            </body></html>
            """;
        var rendered = await new MermaidRenderingService().RenderAsync(
            sourceHtml,
            workspace.PathFor("assets"),
            FindRequiredNpx(),
            FindRequiredBrowser(),
            "required",
            "png",
            CancellationToken.None);

        Assert.Equal(1, rendered.RenderedCount);
        var image = Assert.Single(Directory.EnumerateFiles(workspace.PathFor("assets"), "mermaid-*.png"));
        Assert.True(new FileInfo(image).Length > 1_000);
        Assert.Contains("<figcaption>设备发现与网络配置系统架构时序</figcaption>", rendered.Html, StringComparison.Ordinal);

        var renderedHtml = workspace.WriteText("rendered.html", rendered.Html);
        var numberedHtml = workspace.PathFor("numbered.html");
        await new PandocService().ApplyFigureNumberingAsync(
            FindRequiredPandoc(),
            renderedHtml,
            numberedHtml,
            figureCaptions: true,
            CancellationToken.None);
        var numbered = await File.ReadAllTextAsync(numberedHtml);
        Assert.Contains("图 1.1 设备发现与网络配置系统架构时序", numbered, StringComparison.Ordinal);
    }

    [MermaidE2EFact]
    public async Task PinnedCliRendersSyntheticSequenceDiagramAsSafeSvg()
    {
        using var workspace = new SyntheticWorkspace();
        const string sourceHtml = """
            <!doctype html><html><body>
            <h1>Synthetic chapter</h1>
            <pre class="mermaid"><code>sequenceDiagram
              participant A as Device
              participant B as Network
              A-&gt;&gt;B: Discover</code></pre>
            <!-- caption: Synthetic safe SVG -->
            </body></html>
            """;

        var rendered = await new MermaidRenderingService().RenderAsync(
            sourceHtml,
            workspace.PathFor("assets"),
            FindRequiredNpx(),
            FindRequiredBrowser(),
            "required",
            "svg",
            CancellationToken.None);

        Assert.Equal(1, rendered.RenderedCount);
        var image = Assert.Single(Directory.EnumerateFiles(workspace.PathFor("assets"), "mermaid-*.svg"));
        var svg = await File.ReadAllTextAsync(image);
        Assert.True(new FileInfo(image).Length > 1_000);
        Assert.Contains("<svg", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<foreignObject", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<figcaption>Synthetic safe SVG</figcaption>", rendered.Html, StringComparison.Ordinal);
    }

    private static string FindRequiredNpx() => FindRequiredExecutable(
        "MD2WORD_NPX_PATH",
        OperatingSystem.IsWindows() ? ["npx.cmd", "npx.exe", "npx"] : ["npx"]);

    private static string FindRequiredPandoc() => FindRequiredExecutable(
        "MD2WORD_PANDOC_PATH",
        OperatingSystem.IsWindows() ? ["pandoc.exe", "pandoc"] : ["pandoc"]);

    private static string FindRequiredBrowser()
    {
        var configured = Environment.GetEnvironmentVariable("MD2WORD_MERMAID_BROWSER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in new[]
        {
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(localData, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
        })
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        throw new InvalidOperationException("Microsoft Edge or Google Chrome is required for Mermaid E2E.");
    }

    private static string FindRequiredExecutable(string environmentVariable, IReadOnlyList<string> names)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }
        throw new InvalidOperationException($"Required executable was not found: {string.Join(", ", names)}");
    }
}

internal sealed class MermaidE2EFactAttribute : FactAttribute
{
    public MermaidE2EFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MD2WORD_RUN_MERMAID_E2E"), "1", StringComparison.Ordinal))
        {
            Skip = "Set MD2WORD_RUN_MERMAID_E2E=1 to run the pinned Mermaid CLI with a synthetic diagram.";
        }
    }
}
