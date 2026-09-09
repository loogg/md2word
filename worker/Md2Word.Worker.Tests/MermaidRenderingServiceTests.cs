using System.Text;
using AngleSharp.Html.Parser;
using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class MermaidRenderingServiceTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task RendersCaptionedAndUncaptionedDiagramsWithPinnedSafeArguments()
    {
        using var workspace = new SyntheticWorkspace();
        var npx = workspace.WriteText("npx.cmd", "synthetic executable placeholder");
        var browser = workspace.WriteText("msedge.exe", "synthetic browser placeholder");
        var assets = workspace.PathFor("assets");
        var invocations = new List<MermaidCliInvocation>();
        var runner = new FakeRunner(invocation =>
        {
            invocations.Add(invocation);
            Assert.Contains(MermaidRenderingService.CliPackageSpec, invocation.Arguments);
            Assert.Contains(MermaidRenderingService.PuppeteerPackageSpec, invocation.Arguments);
            Assert.Contains("mmdc", invocation.Arguments);
            Assert.Contains("-c", invocation.Arguments);
            Assert.Contains("-p", invocation.Arguments);
            Assert.Contains("-s", invocation.Arguments);
            Assert.Contains("3", invocation.Arguments);
            Assert.True(File.Exists(ArgumentAfter(invocation.Arguments, "-i")));
            Assert.Contains("securityLevel", File.ReadAllText(ArgumentAfter(invocation.Arguments, "-c")));
            var puppeteerConfiguration = File.ReadAllText(ArgumentAfter(invocation.Arguments, "-p"));
            Assert.Contains("proxy-server=127.0.0.1:9", puppeteerConfiguration);
            Assert.Contains("\"headless\":true", puppeteerConfiguration);
            Assert.Contains("\"enableExtensions\":true", puppeteerConfiguration);
            File.WriteAllBytes(invocation.ExpectedOutputPath!, OnePixelPng);
            return Task.CompletedTask;
        });
        var html = """
            <!doctype html><html><body>
            <pre class="mermaid"><code>sequenceDiagram
            A-&gt;&gt;B: first</code></pre>
            <!-- caption: 设备发现与网络配置系统架构时序 -->
            <pre class="mermaid"><code>flowchart LR
            A--&gt;B</code></pre>
            </body></html>
            """;

        var result = await new MermaidRenderingService(runner).RenderAsync(
            html,
            assets,
            npx,
            browser,
            "required",
            "png",
            CancellationToken.None);

        Assert.Equal(2, result.DiagramCount);
        Assert.Equal(2, result.RenderedCount);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, invocations.Count);
        Assert.Contains("<figure class=\"md2word-mermaid-figure\">", result.Html, StringComparison.Ordinal);
        Assert.Contains("<figcaption>设备发现与网络配置系统架构时序</figcaption>", result.Html, StringComparison.Ordinal);
        Assert.Contains("alt=\"设备发现与网络配置系统架构时序\"", result.Html, StringComparison.Ordinal);
        Assert.Contains($"width=\"{MermaidRenderingService.WordDisplayWidthPixels}\"", result.Html, StringComparison.Ordinal);
        Assert.Contains($"width:{MermaidRenderingService.WordDisplayWidthPixels}px", result.Html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"manual-mermaid-image\"><img", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("caption:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Directory.EnumerateFiles(assets, "mermaid-*.png").Count());
        Assert.DoesNotContain(Directory.EnumerateFiles(assets), path => Path.GetFileName(path).StartsWith('.'));
    }

    [Fact]
    public async Task CaptionMustBeTheImmediateSignificantSibling()
    {
        using var workspace = new SyntheticWorkspace();
        var npx = workspace.WriteText("npx.cmd", "synthetic executable placeholder");
        var browser = workspace.WriteText("msedge.exe", "synthetic browser placeholder");
        var runner = PngRunner();
        var html = """
            <html><body>
            <pre class="mermaid"><code>flowchart LR
            A--&gt;B</code></pre>
            <p>separator</p>
            <!-- caption: must not bind -->
            </body></html>
            """;

        var result = await new MermaidRenderingService(runner).RenderAsync(
            html,
            workspace.PathFor("assets"),
            npx,
            browser,
            "auto",
            "png",
            CancellationToken.None);

        Assert.Equal(1, result.RenderedCount);
        Assert.DoesNotContain("<figure", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual-mermaid-image", result.Html, StringComparison.Ordinal);
        Assert.Contains("caption: must not bind", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoFailureKeepsCodeAndCaptionCommentWithoutOrphanCaption()
    {
        using var workspace = new SyntheticWorkspace();
        var npx = workspace.WriteText("npx.cmd", "synthetic executable placeholder");
        var browser = workspace.WriteText("msedge.exe", "synthetic browser placeholder");
        var runner = new FakeRunner(_ => throw new MermaidCliFailureException(
            "MERMAID_RENDER_FAILED",
            "synthetic failure"));
        var html = """
            <html><body><pre class="mermaid"><code>broken</code></pre>
            <!-- caption: hidden fallback caption --></body></html>
            """;

        var result = await new MermaidRenderingService(runner).RenderAsync(
            html,
            workspace.PathFor("assets"),
            npx,
            browser,
            "auto",
            "png",
            CancellationToken.None);

        Assert.Equal(0, result.RenderedCount);
        Assert.Single(result.Warnings);
        Assert.Contains("MERMAID_RENDER_FAILED", result.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("<pre class=\"mermaid\">", result.Html, StringComparison.Ordinal);
        Assert.Contains("caption: hidden fallback caption", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<figcaption", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequiredFailureUsesStableCodeAndStage()
    {
        using var workspace = new SyntheticWorkspace();
        var npx = workspace.WriteText("npx.cmd", "synthetic executable placeholder");
        var browser = workspace.WriteText("msedge.exe", "synthetic browser placeholder");
        var runner = new FakeRunner(_ => throw new MermaidCliFailureException(
            "MERMAID_RENDER_TIMEOUT",
            "synthetic timeout",
            retryable: true));

        var exception = await Assert.ThrowsAsync<WorkerCommandException>(() =>
            new MermaidRenderingService(runner).RenderAsync(
                "<html><body><pre class=\"mermaid\"><code>broken</code></pre></body></html>",
                workspace.PathFor("assets"),
                npx,
                browser,
                "required",
                "png",
                CancellationToken.None));

        Assert.Equal("MERMAID_RENDER_TIMEOUT", exception.Code);
        Assert.Equal("mermaid", exception.Stage);
        Assert.Equal(4, exception.ExitCode);
        Assert.True(exception.Retryable);
        Assert.DoesNotContain(workspace.Root, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowserLaunchFailureInstallsPinnedManagedFallbackAndRetriesOnce()
    {
        using var workspace = new SyntheticWorkspace();
        var npx = workspace.WriteText("npx.cmd", "synthetic executable placeholder");
        var browser = workspace.WriteText("msedge.exe", "synthetic browser placeholder");
        var calls = 0;
        var runner = new FakeRunner(invocation =>
        {
            calls++;
            if (calls == 1)
            {
                throw new MermaidCliFailureException(
                    "MERMAID_BROWSER_LAUNCH_FAILED",
                    "synthetic system browser failure",
                    retryable: true);
            }
            if (calls == 2)
            {
                Assert.True(invocation.AllowBrowserDownload);
                Assert.Null(invocation.ExpectedOutputPath);
                Assert.Contains(MermaidRenderingService.PuppeteerPackageSpec, invocation.Arguments);
                Assert.Contains(MermaidRenderingService.ProxyAgentPackageSpec, invocation.Arguments);
                Assert.Contains("chrome-headless-shell", invocation.Arguments);
                return Task.CompletedTask;
            }

            Assert.False(invocation.AllowBrowserDownload);
            var configuration = File.ReadAllText(ArgumentAfter(invocation.Arguments, "-p"));
            Assert.Contains("\"headless\":\"shell\"", configuration);
            Assert.DoesNotContain("executablePath", configuration, StringComparison.Ordinal);
            File.WriteAllBytes(invocation.ExpectedOutputPath!, OnePixelPng);
            return Task.CompletedTask;
        });

        var result = await new MermaidRenderingService(runner).RenderAsync(
            "<html><body><pre class=\"mermaid\"><code>sequenceDiagram\nA--&gt;&gt;B: ok</code></pre></body></html>",
            workspace.PathFor("assets"),
            npx,
            browser,
            "required",
            "png",
            CancellationToken.None);

        Assert.Equal(1, result.RenderedCount);
        Assert.Equal(3, runner.CallCount);
    }

    [Theory]
    [InlineData("Error: Failed to launch the browser process: Code: 0", "MERMAID_BROWSER_LAUNCH_FAILED", true)]
    [InlineData("UnknownDiagramError: No diagram type detected", "MERMAID_SOURCE_INVALID", false)]
    [InlineData("npm failed at C:\\private\\cache", "MERMAID_RENDER_FAILED", false)]
    public void CliFailureClassificationIsStableAndDoesNotExposeDiagnostics(
        string standardError,
        string expectedCode,
        bool expectedRetryable)
    {
        var result = ProcessMermaidCliRunner.ClassifyFailure(standardError);

        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(expectedRetryable, result.Retryable);
        Assert.DoesNotContain("C:\\private", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(standardError, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OffModeIsBytePreservingAndDoesNotStartRenderer()
    {
        var runner = new FakeRunner(_ => throw new InvalidOperationException("runner must not start"));
        const string html = "<html><body><pre class=\"mermaid\"><code>A--&gt;B</code></pre><!-- caption: kept --></body></html>";

        var result = await new MermaidRenderingService(runner).RenderAsync(
            html,
            "not-used",
            null,
            null,
            "off",
            "png",
            CancellationToken.None);

        Assert.Equal(html, result.Html);
        Assert.Equal(1, result.DiagramCount);
        Assert.Equal(0, result.RenderedCount);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task IdenticalDiagramsReuseTheValidatedHashAsset()
    {
        using var workspace = new SyntheticWorkspace();
        var npx = workspace.WriteText("npx.cmd", "synthetic executable placeholder");
        var browser = workspace.WriteText("msedge.exe", "synthetic browser placeholder");
        var runner = PngRunner();
        const string html = """
            <html><body>
            <pre class="mermaid"><code>flowchart LR
            A--&gt;B</code></pre>
            <pre class="mermaid"><code>flowchart LR
            A--&gt;B</code></pre>
            </body></html>
            """;

        var result = await new MermaidRenderingService(runner).RenderAsync(
            html,
            workspace.PathFor("assets"),
            npx,
            browser,
            "required",
            "png",
            CancellationToken.None);

        Assert.Equal(2, result.RenderedCount);
        Assert.Equal(1, runner.CallCount);
        var parsed = await new HtmlParser().ParseDocumentAsync(result.Html);
        var sources = parsed.QuerySelectorAll("img").Select(image => image.GetAttribute("src")).ToArray();
        Assert.Equal(2, sources.Length);
        Assert.Equal(sources[0], sources[1]);
    }

    [Fact]
    public async Task MissingNpxDegradesAutoAndBlocksRequired()
    {
        const string html = "<html><body><pre class=\"mermaid\"><code>A--&gt;B</code></pre></body></html>";
        var service = new MermaidRenderingService(new FakeRunner(_ => Task.CompletedTask));

        var automatic = await service.RenderAsync(html, "not-used", null, null, "auto", "png", CancellationToken.None);
        Assert.Equal(0, automatic.RenderedCount);
        Assert.Contains("MERMAID_CLI_MISSING", Assert.Single(automatic.Warnings), StringComparison.Ordinal);

        var required = await Assert.ThrowsAsync<WorkerCommandException>(() =>
            service.RenderAsync(html, "not-used", null, null, "required", "png", CancellationToken.None));
        Assert.Equal("MERMAID_CLI_MISSING", required.Code);
        Assert.Equal(3, required.ExitCode);
    }

    [Fact]
    public async Task RejectsActiveSvgContentAndKeepsAutoFallback()
    {
        using var workspace = new SyntheticWorkspace();
        var npx = workspace.WriteText("npx.cmd", "synthetic executable placeholder");
        var browser = workspace.WriteText("msedge.exe", "synthetic browser placeholder");
        var runner = new FakeRunner(invocation =>
        {
            File.WriteAllText(
                invocation.ExpectedOutputPath!,
                "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>",
                new UTF8Encoding(false));
            return Task.CompletedTask;
        });

        var result = await new MermaidRenderingService(runner).RenderAsync(
            "<html><body><pre class=\"mermaid\"><code>A--&gt;B</code></pre></body></html>",
            workspace.PathFor("assets"),
            npx,
            browser,
            "auto",
            "svg",
            CancellationToken.None);

        Assert.Equal(0, result.RenderedCount);
        Assert.Contains("MERMAID_OUTPUT_INVALID", Assert.Single(result.Warnings), StringComparison.Ordinal);
        Assert.Contains("<pre class=\"mermaid\">", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptionIsPlainTextAndLegacyCationSpellingRemainsCompatible()
    {
        using var workspace = new SyntheticWorkspace();
        var npx = workspace.WriteText("npx.cmd", "synthetic executable placeholder");
        var browser = workspace.WriteText("msedge.exe", "synthetic browser placeholder");
        var html = """
            <html><body><pre class="mermaid"><code>A--&gt;B</code></pre>
            <!-- CATION: synthetic <b>caption</b>
                 second line -->
            </body></html>
            """;

        var result = await new MermaidRenderingService(PngRunner()).RenderAsync(
            html,
            workspace.PathFor("assets"),
            npx,
            browser,
            "required",
            "png",
            CancellationToken.None);

        Assert.Contains("synthetic &lt;b&gt;caption&lt;/b&gt; second line", result.Html, StringComparison.Ordinal);
        var parsed = await new HtmlParser().ParseDocumentAsync(result.Html);
        Assert.Null(parsed.QuerySelector("b"));
        Assert.Equal("synthetic <b>caption</b> second line", parsed.QuerySelector("figcaption")?.TextContent);
    }

    private static FakeRunner PngRunner() => new(invocation =>
    {
        File.WriteAllBytes(invocation.ExpectedOutputPath!, OnePixelPng);
        return Task.CompletedTask;
    });

    private static string ArgumentAfter(IReadOnlyList<string> arguments, string name)
    {
        var index = -1;
        for (var candidate = 0; candidate < arguments.Count; candidate++)
        {
            if (string.Equals(arguments[candidate], name, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }

    private sealed class FakeRunner(Func<MermaidCliInvocation, Task> run) : IMermaidCliRunner
    {
        public int CallCount { get; private set; }

        public Task RunAsync(MermaidCliInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return run(invocation);
        }
    }
}
