using Md2Word.Worker.Services;
using Md2Word.Worker.Protocol;
using AngleSharp.Html.Parser;
using System.Text.RegularExpressions;

namespace Md2Word.Worker.Tests;

public sealed class HtmlConversionTests
{
    [Fact]
    public async Task EmitsRoleMarkersForRepresentativeHeadingBodyCodeAndListContent()
    {
        var css = """
            h2 { mso-style-name: "Heading 2"; }
            p.manual-body-paragraph { mso-style-name: "示例 正文"; }
            ol > li.manual-body-list-item { mso-style-name: "示例 有序列项"; }
            ul > li.manual-body-list-item { mso-style-name: "示例 正文"; }
            p.manual-code-block-paragraph { mso-style-name: "示例 代码"; }
            """;
        var html = """
            <html><head></head><body>
              <h2>Heading</h2>
              <p>Body with <code>inline_code()</code></p>
              <pre><code>Code</code></pre>
              <ol><li>Ordered tight item</li></ol>
              <ul><li><p>Unordered loose item</p></li></ul>
            </body></html>
            """;

        var transformed = await new HtmlConversionService().TransformAsync(
            html,
            css,
            StyleMapParser.Parse(css),
            Path.GetTempPath(),
            CancellationToken.None);

        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Heading2)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Body)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.CodeBlock)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.OrderedList)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.UnorderedList)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(HtmlConversionService.ImportBoundaryRole)));
        Assert.Contains("manual-inline-code", transformed, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(
            transformed,
            @"__MD2WORD_INLINE_CODE_START_[0-9a-f]{32}__",
            RegexOptions.CultureInvariant));
        Assert.Single(Regex.Matches(
            transformed,
            @"__MD2WORD_INLINE_CODE_END_[0-9a-f]{32}__",
            RegexOptions.CultureInvariant));
    }

    [Fact]
    public async Task MarksTightLooseAndNestedListsAndNormalizesCodeBlocks()
    {
        var css = """
            p.manual-body-paragraph { mso-style-name: "示例 正文"; }
            ol > li.manual-body-list-item { mso-style-name: "示例 有序列项"; }
            ul > li.manual-body-list-item { mso-style-name: "示例 正文"; }
            p.manual-code-block-paragraph { mso-style-name: "示例 代码"; }
            """;
        var html = """
            <html><head></head><body>
              <ol><li>tight<ul><li><p>nested loose</p></li></ul></li></ol>
              <pre><code>line 1  gap
                line 2

              line 4</code></pre>
            </body></html>
            """;

        var transformed = await new HtmlConversionService().TransformAsync(
            html,
            css,
            StyleMapParser.Parse(css),
            Path.GetTempPath(),
            CancellationToken.None);

        Assert.Contains("manual-body-ordered-item", transformed);
        Assert.Contains("manual-body-unordered-item", transformed);
        Assert.Contains("manual-body-unordered-item-paragraph", transformed);
        Assert.Contains("manual-code-block-paragraph", transformed);
        Assert.DoesNotContain("<pre", transformed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, Regex.Matches(transformed, "<br", RegexOptions.IgnoreCase).Count);
        Assert.Contains("line 1", transformed, StringComparison.Ordinal);
        Assert.Contains("line 2", transformed, StringComparison.Ordinal);
        Assert.Contains("line 4", transformed, StringComparison.Ordinal);
        Assert.Contains("line 1&nbsp; gap", transformed, StringComparison.Ordinal);
        Assert.Contains("&nbsp;&nbsp;&nbsp;&nbsp;line 2", transformed, StringComparison.Ordinal);
        Assert.Contains("li.manual-body-ordered-item", transformed);
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(HtmlConversionService.ImportBoundaryRole)));
    }

    [Fact]
    public async Task CarriesListContextAcrossEmbeddedCodeAndMarksImageParagraphsAtNearestDepth()
    {
        var css = """
            p.manual-body-paragraph { mso-style-name: "示例 正文"; }
            ol > li.manual-body-list-item { mso-style-name: "示例 有序列项"; }
            ul > li.manual-body-list-item { mso-style-name: "示例 正文"; }
            p.manual-code-block-paragraph { mso-style-name: "示例 代码"; }
            """;
        var sourceDirectory = Path.Combine(Path.GetTempPath(), "md2word synthetic source");
        var html = """
            <html><head></head><body>
              <ol start="4"><li>
                <p>ordered loose paragraph</p>
                <pre><code>ordered code</code></pre>
                <p><img src="pixel.png" alt="synthetic pixel"></p>
                <figure><img src="figure.png" alt="synthetic figure"><figcaption>caption</figcaption></figure>
                <ul><li><pre><code>nested bullet code</code></pre></li></ul>
              </li></ol>
            </body></html>
            """;

        var transformed = await new HtmlConversionService().TransformAsync(
            html,
            css,
            StyleMapParser.Parse(css),
            sourceDirectory,
            CancellationToken.None);

        Assert.Equal(5, Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.OrderedList)).Count);
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.UnorderedList)).Cast<Match>());
        Assert.Equal(2, Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.CodeBlock)).Count);
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Caption)).Cast<Match>());
        Assert.Contains(new Uri(Path.Combine(sourceDirectory, "pixel.png")).AbsoluteUri, transformed);
        Assert.Contains(new Uri(Path.Combine(sourceDirectory, "figure.png")).AbsoluteUri, transformed);
        Assert.Contains("start=\"4\"", transformed, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(HtmlConversionService.ImportBoundaryRole)));
    }

    [Fact]
    public async Task SeparatesCaptionedFigureImageAndCaptionIntoAdjacentParagraphs()
    {
        const string css = """
            p.manual-body-paragraph { mso-style-name: "Body"; }
            p.manual-figure-caption { mso-style-name: "Caption"; }
            """;
        var html = """
            <html><head></head><body>
              <figure>
                <img src="figure.png" alt="synthetic figure">
                <figcaption>图 1.1 Synthetic caption</figcaption>
              </figure>
            </body></html>
            """;

        var transformed = await new HtmlConversionService().TransformAsync(
            html,
            css,
            StyleMapParser.Parse(css),
            Path.GetTempPath(),
            CancellationToken.None);

        Assert.DoesNotContain("<figure", transformed, StringComparison.OrdinalIgnoreCase);
        var imageParagraphIndex = transformed.LastIndexOf("manual-figure-image-paragraph", StringComparison.Ordinal);
        var captionParagraphIndex = transformed.LastIndexOf("manual-figure-caption", StringComparison.Ordinal);
        Assert.True(imageParagraphIndex >= 0);
        Assert.True(captionParagraphIndex > imageParagraphIndex);
        Assert.Contains("text-align: center; margin: 0;", transformed, StringComparison.Ordinal);
        Assert.Contains("figure.png", transformed, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Caption)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.FigureImage)));
        Assert.DoesNotMatch(
            new Regex(@"manual-figure-image-paragraph[^<]*图\s*1\.1", RegexOptions.CultureInvariant),
            transformed);
    }

    [Fact]
    public async Task MarksExtendedWordNativeFallbackRolesAndWrapsPlainTableCells()
    {
        const string html = """
            <html><head></head><body>
              <h5>Heading five</h5>
              <h6>Heading six</h6>
              <div class="admonition admonition-note"><p>Notice text</p></div>
              <table>
                <caption>Table caption</caption>
                <tr><th>Header</th><td>Value <em>with emphasis</em></td></tr>
              </table>
              <dl><dt>Term</dt><dd>Definition</dd></dl>
            </body></html>
            """;

        var transformed = await new HtmlConversionService().TransformAsync(
            html,
            "/* no Word mappings */",
            StyleMapParser.Parse("/* no Word mappings */"),
            Path.GetTempPath(),
            CancellationToken.None);

        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Heading5)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Heading6)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Admonition)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.TableCaption)));
        Assert.Equal(2, Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Table)).Count);
        Assert.Equal(2, Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Body)).Count);
        Assert.Contains("manual-table-caption", transformed, StringComparison.Ordinal);
        Assert.Contains("<th><p class=\"manual-table-paragraph\">", transformed, StringComparison.Ordinal);
        Assert.True(
            transformed.IndexOf("manual-table-caption", StringComparison.Ordinal)
            < transformed.IndexOf("<table", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarksAdmonitionVisualTypesWithoutAddingPublicStyleRoles()
    {
        const string html = """
            <html><head></head><body>
              <div class="admonition"><p>Generic quote</p></div>
              <div class="admonition admonition-note">
                <p>First note paragraph</p>
                <p>Second note paragraph</p>
                <ul><li><p>Nested list paragraph</p></li></ul>
                <pre><code>Nested code block</code></pre>
                <table><tr><td>Nested table cell</td></tr></table>
              </div>
              <div class="admonition admonition-caution"><p>Caution text</p></div>
              <div class="admonition admonition-warning"><p>Warning text</p></div>
              <div class="admonition admonition-note admonition-warning admonition-danger">
                <p>Danger precedence text</p>
              </div>
            </body></html>
            """;

        var transformed = await new HtmlConversionService().TransformAsync(
            html,
            "/* no Word mappings */",
            StyleMapParser.Parse("/* no Word mappings */"),
            Path.GetTempPath(),
            CancellationToken.None);

        Assert.Equal(6, Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Admonition)).Count);
        Assert.Single(Regex.Matches(
            transformed,
            RoleMarkerPattern(HtmlConversionService.AdmonitionVisualGenericRole)));
        Assert.Equal(2, Regex.Matches(
            transformed,
            RoleMarkerPattern(HtmlConversionService.AdmonitionVisualNoteRole)).Count);
        Assert.Single(Regex.Matches(
            transformed,
            RoleMarkerPattern(HtmlConversionService.AdmonitionVisualCautionRole)));
        Assert.Single(Regex.Matches(
            transformed,
            RoleMarkerPattern(HtmlConversionService.AdmonitionVisualWarningRole)));
        Assert.Single(Regex.Matches(
            transformed,
            RoleMarkerPattern(HtmlConversionService.AdmonitionVisualDangerRole)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.UnorderedList)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.CodeBlock)));
        Assert.Single(Regex.Matches(transformed, RoleMarkerPattern(StyleRoles.Table)));
        Assert.DoesNotContain(
            HtmlConversionService.AdmonitionVisualGenericRole,
            StyleRoles.All,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            HtmlConversionService.AdmonitionVisualNoteRole,
            StyleRoles.All,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            HtmlConversionService.AdmonitionVisualCautionRole,
            StyleRoles.All,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            HtmlConversionService.AdmonitionVisualWarningRole,
            StyleRoles.All,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            HtmlConversionService.AdmonitionVisualDangerRole,
            StyleRoles.All,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task BlocksAutomaticRemoteAndInlineCssResourceLoadingButKeepsNormalHyperlinks()
    {
        const string css = "p.manual-body-paragraph { mso-style-name: 'Body'; }";
        var service = new HtmlConversionService();

        var remoteImage = await Assert.ThrowsAsync<WorkerCommandException>(() => service.TransformAsync(
            "<html><body><img src='https://example.invalid/pixel.png'></body></html>",
            css,
            StyleMapParser.Parse(css),
            Path.GetTempPath(),
            CancellationToken.None));
        Assert.Equal("REMOTE_RESOURCE_BLOCKED", remoteImage.Code);

        var inlineStyle = await Assert.ThrowsAsync<WorkerCommandException>(() => service.TransformAsync(
            "<html><body><div style=\"background:url(file://server/share/pixel.png)\">x</div></body></html>",
            css,
            StyleMapParser.Parse(css),
            Path.GetTempPath(),
            CancellationToken.None));
        Assert.Equal("REMOTE_RESOURCE_BLOCKED", inlineStyle.Code);

        var vmlSource = await Assert.ThrowsAsync<WorkerCommandException>(() => service.TransformAsync(
            "<html><body><v:imagedata src='https://example.invalid/vml.png'></v:imagedata></body></html>",
            css,
            StyleMapParser.Parse(css),
            Path.GetTempPath(),
            CancellationToken.None));
        Assert.Equal("UNSAFE_HTML_BLOCKED", vmlSource.Code);

        var normalLink = await service.TransformAsync(
            "<html><body><p><a href='https://example.invalid/help'>help</a></p></body></html>",
            css,
            StyleMapParser.Parse(css),
            Path.GetTempPath(),
            CancellationToken.None);
        Assert.Contains("https://example.invalid/help", normalLink, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RewritesReferencedInternalTargetsToAsciiNamedAnchors()
    {
        const string html = """
            <html><head></head><body>
              <a name="md2word_bm_000001"></a>
              <p><a href="#中文目标">first</a> and <a href="#中文目标">second</a></p>
              <p><a href="#missing">missing</a></p>
              <h2 id="中文目标">Target</h2>
              <h2 id="unreferenced">Unreferenced</h2>
            </body></html>
            """;

        var transformed = await new HtmlConversionService().TransformAsync(
            html,
            "/* no Word mappings */",
            StyleMapParser.Parse("/* no Word mappings */"),
            Path.GetTempPath(),
            CancellationToken.None);
        var document = await new HtmlParser().ParseDocumentAsync(transformed);
        var links = document.QuerySelectorAll("a[href]").ToArray();
        Assert.Equal("#md2word_bm_000002", links[0].GetAttribute("href"));
        Assert.Equal("#md2word_bm_000002", links[1].GetAttribute("href"));
        Assert.Equal("#missing", links[2].GetAttribute("href"));
        Assert.Equal("true", links[0].GetAttribute(HtmlConversionService.InternalLinkMarkerAttribute));
        Assert.Equal("true", links[1].GetAttribute(HtmlConversionService.InternalLinkMarkerAttribute));
        Assert.Null(links[2].GetAttribute(HtmlConversionService.InternalLinkMarkerAttribute));
        Assert.Equal(2, HtmlConversionService.CountGeneratedInternalLinks(transformed));

        var target = Assert.IsAssignableFrom<AngleSharp.Dom.IElement>(document.GetElementById("中文目标"));
        var bookmark = Assert.IsAssignableFrom<AngleSharp.Dom.IElement>(target.PreviousElementSibling);
        Assert.Equal("a", bookmark.LocalName);
        Assert.Equal("md2word_bm_000002", bookmark.GetAttribute("name"));
        Assert.All(
            bookmark.GetAttribute("name")!,
            character => Assert.InRange((int)character, 0, 127));
        Assert.Null(document.GetElementById("unreferenced")!.PreviousElementSibling?.GetAttribute("name"));
    }

    private static string RoleMarkerPattern(string role) =>
        $@"__MD2WORD_ROLE_[0-9a-f]{{32}}_{Regex.Escape(role)}__";
}
