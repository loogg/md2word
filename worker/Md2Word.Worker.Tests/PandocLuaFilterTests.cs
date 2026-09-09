using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class PandocLuaFilterTests
{
    [Fact]
    public void FilterManifestLocksExactBytesAndProductionOrder()
    {
        var manifestPath = ConversionResourceLocator.Get(Path.Combine("filters", "manifest.json"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var filters = manifest.RootElement.GetProperty("filters").EnumerateArray().ToArray();

        Assert.Equal(PandocService.LuaFilterResourceNames, filters.Select(filter => filter.GetProperty("name").GetString()));
        foreach (var filter in filters)
        {
            var name = Assert.IsType<string>(filter.GetProperty("name").GetString());
            var sourceRelativePath = Assert.IsType<string>(filter.GetProperty("sourceRelativePath").GetString());
            var expectedHash = Assert.IsType<string>(filter.GetProperty("sha256").GetString());
            Assert.False(Path.IsPathRooted(sourceRelativePath));
            Assert.DoesNotContain("..", sourceRelativePath, StringComparison.Ordinal);

            var resourcePath = ConversionResourceLocator.Get(Path.Combine("filters", name));
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(resourcePath))).ToLowerInvariant();
            Assert.Equal(expectedHash, actualHash);
        }
    }

    [Fact]
    public async Task HeadingFilterRemapsLevelsAndSkipsNumberingBeforeConfiguredBase()
    {
        var html = await ConvertSyntheticMarkdownAsync(
            """
            ---
            heading_base_level: 2
            heading_numbering_start_base: 2
            ---

            # Removed parent

            ## Synthetic summary

            ### Synthetic summary detail

            ## Synthetic body

            ### Synthetic body detail
            """);

        var h1Tags = OpeningTags(html, "h1");
        var h2Tags = OpeningTags(html, "h2");
        Assert.Equal(2, h1Tags.Count);
        Assert.Equal(2, h2Tags.Count);
        Assert.Contains("unnumbered", h1Tags[0], StringComparison.Ordinal);
        Assert.DoesNotContain("unnumbered", h1Tags[1], StringComparison.Ordinal);
        Assert.Contains("unnumbered", h2Tags[0], StringComparison.Ordinal);
        Assert.DoesNotContain("unnumbered", h2Tags[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Removed parent", html, StringComparison.Ordinal);
    }

    [Fact]
    public void WordHeadingSelectionMatchesTheRemappedStartBaseContract()
    {
        Assert.Equal(
            [0, 1, 2, 3, 4],
            WordAutomationService.SelectNumberedHeadingIndexes([1, 2, 1, 2, 3], startBase: 1));
        Assert.Equal(
            [2, 3, 4],
            WordAutomationService.SelectNumberedHeadingIndexes([1, 2, 1, 2, 3], startBase: 2));
        Assert.Empty(WordAutomationService.SelectNumberedHeadingIndexes([2, 1, 2], startBase: 2));
    }

    [Fact]
    public async Task AdmonitionFilterRecognizesChineseAndEnglishPrefixes()
    {
        var html = await ConvertSyntheticMarkdownAsync(
            """
            > NOTE: Synthetic English note

            > 说明：Synthetic Chinese explanation

            > 提示：Synthetic Chinese tip

            > CAUTION: Synthetic English caution

            > 注意：Synthetic Chinese caution

            > WARNING: Synthetic warning body

            > 警告：Synthetic Chinese warning

            > DANGER: Synthetic English danger

            > 危险：Synthetic Chinese danger
            """);

        Assert.Equal(3, Regex.Matches(html, "class=\"[^\"]*admonition[^\"]*admonition-note[^\"]*\"").Count);
        Assert.Equal(2, Regex.Matches(html, "class=\"[^\"]*admonition[^\"]*admonition-caution[^\"]*\"").Count);
        Assert.Equal(2, Regex.Matches(html, "class=\"[^\"]*admonition[^\"]*admonition-warning[^\"]*\"").Count);
        Assert.Equal(2, Regex.Matches(html, "class=\"[^\"]*admonition[^\"]*admonition-danger[^\"]*\"").Count);
        Assert.Matches("class=\"admonition-label\"[^>]*>NOTE</span>", html);
        Assert.Matches("class=\"admonition-label\"[^>]*>说明</span>", html);
        Assert.Matches("class=\"admonition-label\"[^>]*>提示</span>", html);
        Assert.Matches("class=\"admonition-label\"[^>]*>CAUTION</span>", html);
        Assert.Matches("class=\"admonition-label\"[^>]*>注意</span>", html);
        Assert.Matches("class=\"admonition-label\"[^>]*>WARNING</span>", html);
        Assert.Matches("class=\"admonition-label\"[^>]*>警告</span>", html);
        Assert.Matches("class=\"admonition-label\"[^>]*>DANGER</span>", html);
        Assert.Matches("class=\"admonition-label\"[^>]*>危险</span>", html);
    }

    [Fact]
    public async Task AdmonitionFilterKeepsUnprefixedQuoteGeneric()
    {
        var html = await ConvertSyntheticMarkdownAsync(
            """
            > Synthetic generic quotation

            > Synthetic embedded WARNING: remains generic

            > WARNING without a colon remains generic
            """);

        Assert.Equal(3, Regex.Matches(html, "class=\"admonition\"").Count);
        Assert.DoesNotMatch("admonition-(?:note|caution|warning|danger)", html);
        Assert.DoesNotContain("admonition-label", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TableFilterEmitsAuditedTwoColumnWidthIntent()
    {
        var html = await ConvertSyntheticMarkdownAsync(
            """
            | Key | Description |
            | --- | --- |
            | ID | Synthetic value |
            """);

        Assert.Matches("width:\\s*24(?:\\.0+)?%", html);
        Assert.Matches("width:\\s*76(?:\\.0+)?%", html);
    }

    [Fact]
    public async Task FigureFilterRunsAfterHeadingRemapAndResetsPerRemappedChapter()
    {
        var html = await ConvertSyntheticMarkdownAsync(
            """
            ---
            heading_base_level: 2
            ---

            ## Synthetic chapter one

            ![Synthetic first figure](synthetic-first.png)

            ## Synthetic chapter two

            ![Synthetic second figure](synthetic-second.png)
            """);

        Assert.Contains("图 1.1 Synthetic first figure", html, StringComparison.Ordinal);
        Assert.Contains("图 2.1 Synthetic second figure", html, StringComparison.Ordinal);
        Assert.DoesNotContain("图 1.2 Synthetic second figure", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalFigurePassNumbersOnlySuccessfulFiguresInDocumentOrder()
    {
        using var workspace = new SyntheticWorkspace();
        var input = workspace.WriteText("rendered-figures.html", """
            <!doctype html><html><body>
            <h1>Synthetic chapter</h1>
            <figure><img src="first.png" alt="first"><figcaption>First</figcaption></figure>
            <pre class="mermaid"><code>invalid diagram kept as code</code></pre>
            <!-- caption: Failed diagram must stay hidden -->
            <figure><img src="mermaid.png" alt="Mermaid"><figcaption>Mermaid caption</figcaption></figure>
            <figure><img src="last.png" alt="last"><figcaption>Last</figcaption></figure>
            </body></html>
            """);
        var output = workspace.PathFor("numbered-figures.html");

        await new PandocService().ApplyFigureNumberingAsync(
            FindRequiredPandoc(),
            input,
            output,
            figureCaptions: true,
            CancellationToken.None);

        var html = await File.ReadAllTextAsync(output);
        Assert.Contains("图 1.1 First", html, StringComparison.Ordinal);
        Assert.Contains("图 1.2 Mermaid caption", html, StringComparison.Ordinal);
        Assert.Contains("图 1.3 Last", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed diagram must stay hidden", html, StringComparison.Ordinal);
        Assert.DoesNotContain("图 1.4", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalFigurePassHonorsDisabledFigureCaptions()
    {
        using var workspace = new SyntheticWorkspace();
        var input = workspace.WriteText("caption-disabled.html", """
            <!doctype html><html><body>
            <h1>Synthetic chapter</h1>
            <figure><img src="mermaid.png" alt="Mermaid"><figcaption>Hidden caption</figcaption></figure>
            </body></html>
            """);
        var output = workspace.PathFor("caption-disabled-output.html");

        await new PandocService().ApplyFigureNumberingAsync(
            FindRequiredPandoc(),
            input,
            output,
            figureCaptions: false,
            CancellationToken.None);

        var html = await File.ReadAllTextAsync(output);
        Assert.DoesNotContain("Hidden caption", html, StringComparison.Ordinal);
        Assert.DoesNotContain("图 1.1", html, StringComparison.Ordinal);
        Assert.Contains("<img", html, StringComparison.Ordinal);
    }

    private static List<string> OpeningTags(string html, string tagName) => Regex.Matches(
            html,
            $"<{Regex.Escape(tagName)}\\b[^>]*>",
            RegexOptions.IgnoreCase)
        .Select(match => match.Value)
        .ToList();

    private static async Task<string> ConvertSyntheticMarkdownAsync(string markdown)
    {
        using var workspace = new SyntheticWorkspace();
        var sourcePath = workspace.WriteText("synthetic-filter-input.md", markdown);
        var semanticOutputPath = workspace.PathFor("synthetic-filter-semantic.html");
        var outputPath = workspace.PathFor("synthetic-filter-output.html");
        var service = new PandocService();
        var pandoc = FindRequiredPandoc();
        await service.ConvertToHtmlAsync(
            pandoc,
            sourcePath,
            semanticOutputPath,
            3,
            CancellationToken.None);
        var metadata = await new PandocMetadataReader().ReadAsync(pandoc, sourcePath, CancellationToken.None);
        await service.ApplyFigureNumberingAsync(
            pandoc,
            semanticOutputPath,
            outputPath,
            metadata.FigureCaptions,
            CancellationToken.None);
        return await File.ReadAllTextAsync(outputPath);
    }

    private static string FindRequiredPandoc()
    {
        var configured = Environment.GetEnvironmentVariable("MD2WORD_PANDOC_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var executableNames = OperatingSystem.IsWindows()
            ? new[] { "pandoc.exe", "pandoc" }
            : new[] { "pandoc" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var executableName in executableNames)
            {
                var candidate = Path.Combine(directory.Trim('"'), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException("Pandoc is required to verify the checked-in Lua filters.");
    }
}
