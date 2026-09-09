using System.Diagnostics;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

[Collection("Word COM E2E")]
public sealed class MermaidWordEndToEndTests
{
    private const long MaximumFigureWidthEmus = 5_800_000;

    [MermaidWordE2EFact]
    public void ConvertsMermaidToEmbeddedPngAndAppliesCssCaptionStyle()
    {
        AssertConversion("png", "image/png");
    }

    [MermaidWordE2EFact]
    public void ConvertsMermaidToEmbeddedSafeSvgAndAppliesCssCaptionStyle()
    {
        AssertConversion("svg", "image/svg+xml");
    }

    private static void AssertConversion(string format, string expectedContentType)
    {
        var existingWordProcesses = GetWordProcessIds();
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("Body", "正文", null),
            ("Ordered", "示例 有序列项", null),
            ("Caption", "图注", null),
            ("Code", "CodeBlock", null),
        ]);
        var css = workspace.WriteText("style.css", """
            p.manual-body-paragraph { mso-style-name: "正文"; }
            p.manual-table-paragraph { mso-style-name: "正文"; }
            ol > li.manual-body-list-item { mso-style-name: "示例 有序列项"; }
            ul > li.manual-body-list-item { mso-style-name: "正文"; }
            p.manual-figure-caption { mso-style-name: "图注"; }
            p.manual-code-block-paragraph { mso-style-name: "CodeBlock"; }
            """);
        var markdown = workspace.WriteText("mermaid-caption.md", """
            # Synthetic chapter

            ```mermaid
            sequenceDiagram
              participant A as Device
              participant B as Network
              A->>B: Discover
              B-->>A: Configure
            ```
            <!-- caption: 设备发现与网络配置系统架构时序 -->
            """);
        var output = workspace.PathFor($"mermaid-{format}-output.docx");
        var stages = new List<string>();

        var result = new ConversionService().Convert(
            new ConversionRequest(
                "mermaid-word-e2e",
                "synthetic-template",
                markdown,
                output,
                new TemplateSnapshot(
                    template,
                    css,
                    TemplateValidationService.ComputeFingerprint(template, css)),
                new ToolPaths(FindRequiredPandoc(), FindRequiredNpx(), FindRequiredBrowser()),
                new ConversionOptions(3, "required", format)),
            (stage, _) => stages.Add(stage),
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Contains("mermaid", stages);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("remains a code block", StringComparison.OrdinalIgnoreCase));
        using (var document = WordprocessingDocument.Open(output, false))
        {
            var mainPart = document.MainDocumentPart!;
            Assert.Empty(mainPart.ExternalRelationships);
            var imagePart = Assert.Single(mainPart.ImageParts, part => part.ContentType == expectedContentType);
            using (var imageStream = imagePart.GetStream(FileMode.Open, FileAccess.Read))
            {
                Assert.True(imageStream.Length > 1_000);
            }

            var drawingExtent = Assert.Single(mainPart.Document!.Body!.Descendants<DW.Extent>());
            Assert.NotNull(drawingExtent.Cx);
            Assert.InRange(drawingExtent.Cx!.Value, 1L, MaximumFigureWidthEmus);

            var caption = Assert.Single(mainPart.Document.Body.Descendants<Paragraph>(), paragraph =>
                paragraph.InnerText.Contains("设备发现与网络配置系统架构时序", StringComparison.Ordinal));
            var captionStyleId = Assert.IsType<string>(caption.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
            var captionStyle = Assert.Single(
                mainPart.StyleDefinitionsPart!.Styles!.Elements<Style>(),
                style => string.Equals(style.StyleId?.Value, captionStyleId, StringComparison.Ordinal));
            Assert.Contains(
                captionStyle.StyleName?.Val?.Value,
                new[] { "caption", "图注", "题注" },
                StringComparer.OrdinalIgnoreCase);
            Assert.Contains("图 1.1 设备发现与网络配置系统架构时序", caption.InnerText, StringComparison.Ordinal);
            Assert.Empty(caption.Descendants<Drawing>());
            var imageParagraph = Assert.Single(mainPart.Document.Body.Descendants<Paragraph>(), paragraph =>
                paragraph.Descendants<Drawing>().Any());
            Assert.DoesNotContain("图 1.1", imageParagraph.InnerText, StringComparison.Ordinal);
            Assert.DoesNotContain("sequenceDiagram", mainPart.Document.Body.InnerText, StringComparison.Ordinal);
        }

        AssertNoNewWordProcessRemains(existingWordProcesses);
    }

    private static HashSet<int> GetWordProcessIds() => Process.GetProcessesByName("WINWORD")
        .Select(process =>
        {
            try { return process.Id; }
            finally { process.Dispose(); }
        })
        .ToHashSet();

    private static void AssertNoNewWordProcessRemains(IReadOnlySet<int> existing)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var added = GetWordProcessIds().Where(id => !existing.Contains(id)).ToArray();
            if (added.Length == 0) return;
            Thread.Sleep(250);
        }
        Assert.DoesNotContain(GetWordProcessIds(), id => !existing.Contains(id));
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
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
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
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new InvalidOperationException("Microsoft Edge or Google Chrome is required for Mermaid Word E2E.");
    }

    private static string FindRequiredExecutable(string environmentVariable, IReadOnlyList<string> names)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        throw new InvalidOperationException($"Required executable was not found: {string.Join(", ", names)}");
    }
}

internal sealed class MermaidWordE2EFactAttribute : FactAttribute
{
    public MermaidWordE2EFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MD2WORD_RUN_WORD_E2E"), "1", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("MD2WORD_RUN_MERMAID_E2E"), "1", StringComparison.Ordinal))
        {
            Skip = "Set MD2WORD_RUN_WORD_E2E=1 and MD2WORD_RUN_MERMAID_E2E=1 to run synthetic Mermaid through Word.";
        }
    }
}
