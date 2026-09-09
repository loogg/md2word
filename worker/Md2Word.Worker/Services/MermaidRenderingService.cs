using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal sealed record MermaidRenderingResult(
    string Html,
    int DiagramCount,
    int RenderedCount,
    IReadOnlyList<string> Warnings);

internal sealed record MermaidCliInvocation(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? ExpectedOutputPath,
    bool AllowBrowserDownload = false);

internal sealed record MermaidCliFailureClassification(string Code, string Message, bool Retryable);

internal interface IMermaidCliRunner
{
    Task RunAsync(MermaidCliInvocation invocation, CancellationToken cancellationToken);
}

internal sealed class MermaidCliFailureException(string code, string message, bool retryable = false, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

internal sealed class ProcessMermaidCliRunner : IMermaidCliRunner
{
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan BrowserInstallTimeout = TimeSpan.FromMinutes(15);

    public async Task RunAsync(MermaidCliInvocation invocation, CancellationToken cancellationToken)
    {
        var executablePath = invocation.ExecutablePath;
        string? npxCliPath = null;
        if (OperatingSystem.IsWindows()
            && string.Equals(Path.GetFileName(invocation.ExecutablePath), "npx.cmd", StringComparison.OrdinalIgnoreCase))
        {
            var npxDirectory = Path.GetDirectoryName(invocation.ExecutablePath);
            var nodeCandidate = npxDirectory is null ? null : Path.Combine(npxDirectory, "node.exe");
            var cliCandidate = npxDirectory is null
                ? null
                : Path.Combine(npxDirectory, "node_modules", "npm", "bin", "npx-cli.js");
            if (nodeCandidate is not null && cliCandidate is not null
                && File.Exists(nodeCandidate) && File.Exists(cliCandidate))
            {
                executablePath = nodeCandidate;
                npxCliPath = cliCandidate;
            }
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (npxCliPath is not null)
        {
            startInfo.ArgumentList.Add(npxCliPath);
        }
        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("npm_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }
        startInfo.Environment.Remove("INIT_CWD");
        // Windows app-compat launchers can inject this marker into the parent
        // process. Edge then relaunches through the compatibility layer and the
        // original process exits with code 0 before Puppeteer can attach.
        startInfo.Environment.Remove("__COMPAT_LAYER");
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key is "NODE" or "NODE_ENV"
                         || key.StartsWith("PLAYWRIGHT_", StringComparison.OrdinalIgnoreCase)
                         || key.StartsWith("PW_", StringComparison.OrdinalIgnoreCase)
                         || key.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }
        startInfo.Environment["NO_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["npm_config_audit"] = "false";
        startInfo.Environment["npm_config_fund"] = "false";
        var rendererNpmCachePath = ResolveConfiguredDirectory("MD2WORD_MERMAID_NPM_CACHE");
        var operationNpmCachePath = invocation.AllowBrowserDownload && rendererNpmCachePath is not null
            ? ResolveDirectory(Path.Combine(Path.GetDirectoryName(rendererNpmCachePath)!, "mermaid-browser-npm-cache"))
            : rendererNpmCachePath;
        if (operationNpmCachePath is not null)
        {
            startInfo.Environment["npm_config_cache"] = operationNpmCachePath;
        }
        var browserCachePath = ResolveConfiguredDirectory("MD2WORD_MERMAID_BROWSER_CACHE")
            ?? (rendererNpmCachePath is null
                ? null
                : ResolveDirectory(Path.Combine(Path.GetDirectoryName(rendererNpmCachePath)!, "mermaid-browser-cache")));
        if (browserCachePath is not null)
        {
            startInfo.Environment["PUPPETEER_CACHE_DIR"] = browserCachePath;
        }
        startInfo.Environment.Remove("PUPPETEER_EXECUTABLE_PATH");
        // Keep npm package installation deterministic and lightweight. The
        // explicit `puppeteer browsers install chrome-headless-shell` fallback
        // still downloads the named pinned browser when requested.
        startInfo.Environment["PUPPETEER_SKIP_DOWNLOAD"] = "true";
        startInfo.Environment["PUPPETEER_SKIP_CHROME_DOWNLOAD"] = "true";
        startInfo.Environment["NODE_OPTIONS"] = string.Empty;
        startInfo.Environment["NODE_PATH"] = string.Empty;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new MermaidCliFailureException(
                    "MERMAID_CLI_MISSING",
                    "The Mermaid renderer could not be started.",
                    retryable: true);
            }
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new MermaidCliFailureException(
                "MERMAID_CLI_MISSING",
                "The Mermaid renderer could not be started.",
                retryable: true,
                inner: exception);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operationTimeout = invocation.AllowBrowserDownload ? BrowserInstallTimeout : RenderTimeout;
        timeout.CancelAfter(operationTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await AwaitProcessExitAsync(process).ConfigureAwait(false);
            throw new MermaidCliFailureException(
                invocation.AllowBrowserDownload ? "MERMAID_BROWSER_INSTALL_TIMEOUT" : "MERMAID_RENDER_TIMEOUT",
                invocation.AllowBrowserDownload
                    ? "The managed Mermaid browser download exceeded its 15 minute limit."
                    : "The Mermaid renderer exceeded its 180 second limit.",
                retryable: true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitProcessExitAsync(process).ConfigureAwait(false);
            throw;
        }

        _ = await stdout.ConfigureAwait(false);
        var standardError = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var classification = ClassifyFailure(standardError);
            throw new MermaidCliFailureException(
                classification.Code,
                classification.Message,
                classification.Retryable);
        }
        if (invocation.ExpectedOutputPath is not null && !File.Exists(invocation.ExpectedOutputPath))
        {
            throw new MermaidCliFailureException(
                "MERMAID_RENDER_FAILED",
                "Mermaid CLI did not produce a valid diagram image.");
        }
    }

    internal static MermaidCliFailureClassification ClassifyFailure(string standardError)
    {
        if (Regex.IsMatch(
            standardError,
            @"failed to launch the browser process|could not find (?:chrome|chromium|browser)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return new MermaidCliFailureClassification(
                "MERMAID_BROWSER_LAUNCH_FAILED",
                "The local Mermaid browser could not be launched.",
                true);
        }
        if (Regex.IsMatch(
            standardError,
            @"parse error|unknowndiagramerror|lexical error",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return new MermaidCliFailureClassification(
                "MERMAID_SOURCE_INVALID",
                "The Mermaid source could not be parsed.",
                false);
        }
        return new MermaidCliFailureClassification(
            "MERMAID_RENDER_FAILED",
            "Mermaid CLI did not produce a valid diagram image.",
            false);
    }

    private static string? ResolveConfiguredDirectory(string environmentVariable)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
        {
            return null;
        }
        return ResolveDirectory(configured);
    }

    private static string? ResolveDirectory(string configured)
    {
        try
        {
            var fullPath = Path.GetFullPath(configured);
            if (fullPath.StartsWith("\\\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
            {
                return null;
            }
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process already exited or cannot be reached after cancellation.
        }
    }

    private static async Task AwaitProcessExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            // Do not mask the stable timeout/cancellation result.
        }
    }
}

internal sealed class MermaidRenderingService(IMermaidCliRunner? cliRunner = null)
{
    internal const string CliPackageSpec = "@mermaid-js/mermaid-cli@11.16.0";
    internal const string PuppeteerPackageSpec = "puppeteer@25.3.0";
    internal const string ProxyAgentPackageSpec = "proxy-agent@6.5.0";
    internal const int WordDisplayWidthPixels = 600;
    private const string RendererConfigurationVersion = "md2word-mermaid-v3";
    private const int MaxDiagrams = 64;
    private const int MaxSourceBytes = 256 * 1024;
    private const int MaxCaptionCharacters = 4096;
    private const long MaxOutputBytes = 20L * 1024 * 1024;
    private readonly IMermaidCliRunner cliRunner = cliRunner ?? new ProcessMermaidCliRunner();
    private bool managedBrowserPrepared;

    public async Task<MermaidRenderingResult> RenderAsync(
        string html,
        string assetsDirectory,
        string? npxPath,
        string? browserPath,
        string mode,
        string format,
        CancellationToken cancellationToken)
    {
        if (mode is not ("auto" or "off" or "required") || format is not ("png" or "svg"))
        {
            throw new WorkerCommandException(
                "REQUEST_INVALID",
                "Mermaid options contain an unsupported value.",
                2,
                "mermaid");
        }

        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        var diagrams = document.QuerySelectorAll("pre.mermaid").ToArray();
        if (mode == "off" || diagrams.Length == 0)
        {
            return new MermaidRenderingResult(html, diagrams.Length, 0, Array.Empty<string>());
        }

        if (diagrams.Length > MaxDiagrams)
        {
            var failure = new MermaidCliFailureException(
                "MERMAID_LIMIT_EXCEEDED",
                $"A document may contain at most {MaxDiagrams} Mermaid diagrams.");
            return HandleWholeDocumentFailure(html, diagrams.Length, mode, failure);
        }
        if (string.IsNullOrWhiteSpace(npxPath)
            || !Path.IsPathFullyQualified(npxPath)
            || !File.Exists(npxPath))
        {
            var failure = new MermaidCliFailureException(
                "MERMAID_CLI_MISSING",
                "npx is required to run the pinned Mermaid renderer.",
                retryable: true);
            return HandleWholeDocumentFailure(html, diagrams.Length, mode, failure);
        }
        if (string.IsNullOrWhiteSpace(browserPath)
            || !Path.IsPathFullyQualified(browserPath)
            || !File.Exists(browserPath))
        {
            var failure = new MermaidCliFailureException(
                "MERMAID_BROWSER_MISSING",
                "Microsoft Edge or Google Chrome is required to render Mermaid diagrams.",
                retryable: true);
            return HandleWholeDocumentFailure(html, diagrams.Length, mode, failure);
        }

        var assetsRoot = Path.GetFullPath(assetsDirectory);
        Directory.CreateDirectory(assetsRoot);
        var warnings = new List<string>();
        var rendered = 0;
        for (var diagramIndex = 0; diagramIndex < diagrams.Length; diagramIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preformatted = diagrams[diagramIndex];
            var source = NormalizeSource(
                preformatted.Children.FirstOrDefault(child => child.LocalName == "code")?.TextContent
                ?? preformatted.TextContent);
            var caption = FindAdjacentCaption(preformatted);
            try
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    throw new MermaidCliFailureException(
                        "MERMAID_SOURCE_EMPTY",
                        "The Mermaid code block is empty.");
                }
                if (Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
                {
                    throw new MermaidCliFailureException(
                        "MERMAID_LIMIT_EXCEEDED",
                        $"A Mermaid diagram may contain at most {MaxSourceBytes} UTF-8 bytes.");
                }
                if (caption.Text is { Length: > MaxCaptionCharacters })
                {
                    throw new MermaidCliFailureException(
                        "MERMAID_LIMIT_EXCEEDED",
                        $"A Mermaid caption may contain at most {MaxCaptionCharacters} characters.");
                }

                var imagePath = await RenderOneAsync(
                    source,
                    format,
                    npxPath,
                    browserPath,
                    assetsRoot,
                    cancellationToken).ConfigureAwait(false);
                ReplaceWithImage(document, preformatted, caption, imagePath, diagramIndex + 1);
                rendered++;
            }
            catch (MermaidCliFailureException failure)
            {
                if (mode == "required")
                {
                    throw RequiredFailure(failure, diagramIndex + 1);
                }
                warnings.Add($"Mermaid diagram {diagramIndex + 1} remains a code block ({failure.Code}).");
            }
        }

        return new MermaidRenderingResult(
            document.DocumentElement.OuterHtml,
            diagrams.Length,
            rendered,
            warnings);
    }

    private async Task<string> RenderOneAsync(
        string source,
        string format,
        string npxPath,
        string browserPath,
        string assetsRoot,
        CancellationToken cancellationToken)
    {
        var mermaidConfiguration = format == "svg"
            ? "{\"securityLevel\":\"strict\",\"htmlLabels\":false}"
            : "{\"securityLevel\":\"strict\"}";
        var puppeteerConfiguration = BuildPuppeteerConfiguration(browserPath);
        var managedPuppeteerConfiguration = BuildManagedPuppeteerConfiguration();
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            RendererConfigurationVersion,
            CliPackageSpec,
            PuppeteerPackageSpec,
            format,
            mermaidConfiguration,
            puppeteerConfiguration,
            managedPuppeteerConfiguration,
            source)))).ToLowerInvariant();
        var finalPath = Path.Combine(assetsRoot, $"mermaid-{cacheKey[..24]}.{format}");
        if (File.Exists(finalPath))
        {
            ValidateOutput(finalPath, format);
            return finalPath;
        }

        var operationId = Guid.NewGuid().ToString("N");
        var sourcePath = Path.Combine(assetsRoot, $".mermaid-{operationId}.mmd");
        var mermaidConfigPath = Path.Combine(assetsRoot, $".mermaid-{operationId}.json");
        var puppeteerConfigPath = Path.Combine(assetsRoot, $".puppeteer-{operationId}.json");
        var temporaryOutputPath = Path.Combine(assetsRoot, $".mermaid-{operationId}.{format}");
        try
        {
            await File.WriteAllTextAsync(sourcePath, source, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(mermaidConfigPath, mermaidConfiguration, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(puppeteerConfigPath, puppeteerConfiguration, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var arguments = new List<string>
            {
                "--yes",
                "--package", CliPackageSpec,
                "--package", PuppeteerPackageSpec,
                "mmdc",
                "-i", sourcePath,
                "-o", temporaryOutputPath,
                "-e", format,
                "-b", "transparent",
                "-q",
                "-c", mermaidConfigPath,
                "-p", puppeteerConfigPath,
            };
            if (format == "png")
            {
                arguments.AddRange(["-s", "3"]);
            }
            try
            {
                await cliRunner.RunAsync(
                    new MermaidCliInvocation(npxPath, arguments, assetsRoot, temporaryOutputPath),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (MermaidCliFailureException failure) when (failure.Code == "MERMAID_BROWSER_LAUNCH_FAILED")
            {
                await EnsureManagedBrowserAsync(npxPath, assetsRoot, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    puppeteerConfigPath,
                    managedPuppeteerConfiguration,
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
                await cliRunner.RunAsync(
                    new MermaidCliInvocation(npxPath, arguments, assetsRoot, temporaryOutputPath),
                    cancellationToken).ConfigureAwait(false);
            }
            ValidateOutput(temporaryOutputPath, format);
            try
            {
                File.Move(temporaryOutputPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                ValidateOutput(finalPath, format);
            }
            return finalPath;
        }
        catch (MermaidCliFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            throw new MermaidCliFailureException(
                "MERMAID_OUTPUT_INVALID",
                "The Mermaid renderer produced an unreadable or unsafe image.",
                inner: exception);
        }
        finally
        {
            TryDelete(sourcePath);
            TryDelete(mermaidConfigPath);
            TryDelete(puppeteerConfigPath);
            TryDelete(temporaryOutputPath);
        }
    }

    private async Task EnsureManagedBrowserAsync(
        string npxPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (managedBrowserPrepared)
        {
            return;
        }
        try
        {
            await cliRunner.RunAsync(
                new MermaidCliInvocation(
                    npxPath,
                    [
                        "--yes",
                        "--package", PuppeteerPackageSpec,
                        // @puppeteer/browsers loads proxy-agent dynamically. Keep it
                        // alongside the installer so HTTP(S)_PROXY is honored instead
                        // of silently attempting a direct browser-binary download.
                        "--package", ProxyAgentPackageSpec,
                        "puppeteer",
                        "browsers",
                        "install",
                        "chrome-headless-shell",
                    ],
                    workingDirectory,
                    ExpectedOutputPath: null,
                    AllowBrowserDownload: true),
                cancellationToken).ConfigureAwait(false);
            managedBrowserPrepared = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MermaidCliFailureException exception)
        {
            throw new MermaidCliFailureException(
                "MERMAID_BROWSER_INSTALL_FAILED",
                "The managed Mermaid browser could not be prepared.",
                retryable: true,
                inner: exception);
        }
    }

    private static void ValidateOutput(string path, string format)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > MaxOutputBytes)
        {
            throw new MermaidCliFailureException(
                "MERMAID_OUTPUT_INVALID",
                "The Mermaid renderer produced an empty or oversized image.");
        }
        if (format == "png")
        {
            ValidatePng(path);
        }
        else
        {
            ValidateAndNormalizeSvg(path);
        }
    }

    private static void ValidatePng(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Read(header) != header.Length
            || !header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
            || !header[12..16].SequenceEqual("IHDR"u8))
        {
            throw new MermaidCliFailureException(
                "MERMAID_OUTPUT_INVALID",
                "The Mermaid PNG signature is invalid.");
        }
        var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (width is 0 or > 100_000 || height is 0 or > 100_000)
        {
            throw new MermaidCliFailureException(
                "MERMAID_OUTPUT_INVALID",
                "The Mermaid PNG dimensions are invalid.");
        }
    }

    private static void ValidateAndNormalizeSvg(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxOutputBytes,
        };
        XDocument document;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = XmlReader.Create(stream, settings))
        {
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        if (document.Root is null || !string.Equals(document.Root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new MermaidCliFailureException(
                "MERMAID_OUTPUT_INVALID",
                "The Mermaid SVG root element is invalid.");
        }
        var blockedElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "script", "foreignObject", "iframe", "object", "embed",
        };
        foreach (var element in document.Descendants())
        {
            if (blockedElements.Contains(element.Name.LocalName))
            {
                throw new MermaidCliFailureException(
                    "MERMAID_OUTPUT_INVALID",
                    "The Mermaid SVG contains active content.");
            }
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                {
                    throw new MermaidCliFailureException(
                        "MERMAID_OUTPUT_INVALID",
                        "The Mermaid SVG contains an event handler.");
                }
                if (attribute.Name.LocalName is "href" or "src"
                    && !string.IsNullOrWhiteSpace(attribute.Value)
                    && !attribute.Value.TrimStart().StartsWith('#'))
                {
                    throw new MermaidCliFailureException(
                        "MERMAID_OUTPUT_INVALID",
                        "The Mermaid SVG contains an external reference.");
                }
                if (attribute.Name.LocalName == "style")
                {
                    RejectUnsafeSvgCss(attribute.Value);
                }
            }
            if (element.Name.LocalName == "style")
            {
                RejectUnsafeSvgCss(element.Value);
            }
        }
        var svg = File.ReadAllText(path).Replace("stroke-dasharray:0;", "stroke-dasharray:none;", StringComparison.Ordinal);
        File.WriteAllText(path, svg, new UTF8Encoding(false));
    }

    private static void RejectUnsafeSvgCss(string css)
    {
        if (Regex.IsMatch(css, @"@\s*import\b", RegexOptions.IgnoreCase))
        {
            throw new MermaidCliFailureException(
                "MERMAID_OUTPUT_INVALID",
                "The Mermaid SVG contains an external stylesheet.");
        }
        foreach (Match match in Regex.Matches(css, "url\\s*\\(\\s*['\\\"]?(?<value>[^)'\\\"]+)", RegexOptions.IgnoreCase))
        {
            if (!match.Groups["value"].Value.Trim().StartsWith('#'))
            {
                throw new MermaidCliFailureException(
                    "MERMAID_OUTPUT_INVALID",
                    "The Mermaid SVG contains an external resource.");
            }
        }
    }

    private static string BuildPuppeteerConfiguration(string browserPath) => JsonSerializer.Serialize(new
    {
        executablePath = Path.GetFullPath(browserPath),
        headless = true,
        enableExtensions = true,
        args = new[]
        {
            "--proxy-server=127.0.0.1:9",
            "--proxy-bypass-list=<-loopback>",
        },
    });

    private static string BuildManagedPuppeteerConfiguration() => JsonSerializer.Serialize(new
    {
        headless = "shell",
        args = new[]
        {
            "--proxy-server=127.0.0.1:9",
            "--proxy-bypass-list=<-loopback>",
        },
    });

    private static (IComment? Node, string? Text) FindAdjacentCaption(IElement preformatted)
    {
        INode? sibling = preformatted.ParentElement is { } parent && parent.ClassList.Contains("sourceCode")
            ? parent.NextSibling
            : preformatted.NextSibling;
        while (sibling is IText text && string.IsNullOrWhiteSpace(text.Data))
        {
            sibling = sibling.NextSibling;
        }
        if (sibling is not IComment comment)
        {
            return (null, null);
        }
        var match = Regex.Match(
            comment.Data,
            @"^\s*(?:caption|cation)\s*:\s*(?<caption>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return (null, null);
        }
        var caption = Regex.Replace(match.Groups["caption"].Value, @"\s+", " ").Trim();
        return caption.Length == 0 ? (null, null) : (comment, caption);
    }

    private static void ReplaceWithImage(
        IDocument document,
        IElement preformatted,
        (IComment? Node, string? Text) caption,
        string imagePath,
        int diagramIndex)
    {
        var image = document.CreateElement("img");
        image.SetAttribute("src", new Uri(Path.GetFullPath(imagePath)).AbsoluteUri);
        image.SetAttribute("alt", caption.Text ?? $"Mermaid diagram {diagramIndex}");
        image.SetAttribute("width", WordDisplayWidthPixels.ToString(System.Globalization.CultureInfo.InvariantCulture));
        image.SetAttribute("style", $"width:{WordDisplayWidthPixels}px;max-width:100%;height:auto;");

        IElement replacement;
        if (caption.Text is not null)
        {
            replacement = document.CreateElement("figure");
            replacement.ClassList.Add("md2word-mermaid-figure");
            replacement.AppendChild(image);
            var figcaption = document.CreateElement("figcaption");
            figcaption.TextContent = caption.Text;
            replacement.AppendChild(figcaption);
        }
        else
        {
            replacement = document.CreateElement("p");
            replacement.ClassList.Add("manual-mermaid-image");
            replacement.AppendChild(image);
        }

        var sourceContainer = preformatted.ParentElement is { } parent && parent.ClassList.Contains("sourceCode")
            ? parent
            : preformatted;
        sourceContainer.Replace(replacement);
        caption.Node?.Remove();
    }

    private static string NormalizeSource(string source) => source
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Trim('\n');

    private static MermaidRenderingResult HandleWholeDocumentFailure(
        string html,
        int diagramCount,
        string mode,
        MermaidCliFailureException failure)
    {
        if (mode == "required")
        {
            throw RequiredFailure(failure, 1);
        }
        return new MermaidRenderingResult(
            html,
            diagramCount,
            0,
            [$"Mermaid diagrams remain code blocks ({failure.Code})."]);
    }

    private static WorkerCommandException RequiredFailure(MermaidCliFailureException failure, int diagramIndex) => new(
        failure.Code,
        $"Mermaid diagram {diagramIndex} is required but could not be rendered.",
        failure.Code is "MERMAID_CLI_MISSING" or "MERMAID_BROWSER_MISSING" ? 3 : 4,
        "mermaid",
        failure.Retryable,
        failure);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The parent job directory is removed by ConversionService as a final cleanup boundary.
        }
    }
}
