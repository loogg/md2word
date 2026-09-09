using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlInlineCodeFinalizerTests
{
    [Fact]
    public void RemovesHtmlCodeRunStyleAndMapsInlineRangeToBodyParagraphStyle()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("inline-code.docx");
        CreateDocument(path, paragraphStyleId: "ExampleBody");

        var result = OpenXmlInlineCodeFinalizer.Finalize(
            path,
            new Dictionary<string, ResolvedStyle>
            {
                [StyleRoles.InlineCode] = new("ExampleBody", "示例 正文"),
            },
            bodyOnly: true);

        Assert.Equal(2, result.MarkersRemoved);
        Assert.Equal(1, result.RangesStyled);
        Assert.True(result.RunsStyled >= 1);
        Assert.Equal(1, result.HtmlCodeStylesRemoved);

        using var document = WordprocessingDocument.Open(path, false);
        var paragraph = document.MainDocumentPart!.Document!.Body!
            .Elements<Paragraph>()
            .Single(candidate => candidate.InnerText.Contains("inline_code()", StringComparison.Ordinal));
        Assert.Equal("before inline_code() after", paragraph.InnerText);
        Assert.DoesNotContain("MD2WORD_INLINE_CODE", paragraph.InnerText, StringComparison.Ordinal);
        var inlineRun = paragraph.Descendants<Run>()
            .Single(run => run.InnerText == "inline_code()");
        Assert.Null(inlineRun.RunProperties?.RunStyle);
        Assert.Null(inlineRun.RunProperties?.RunFonts);
        Assert.Null(inlineRun.RunProperties?.Shading);
        Assert.Equal("ExampleBody", paragraph.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.DoesNotContain(
            document.MainDocumentPart.StyleDefinitionsPart!.Styles!.Elements<Style>(),
            style => style.StyleId?.Value == "HTMLCode");
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    [Fact]
    public void ProjectsBodyRunPropertiesWhenInlineCodeAppearsInAnotherParagraphStyle()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("inline-code-heading.docx");
        CreateDocument(path, paragraphStyleId: "Heading1");

        OpenXmlInlineCodeFinalizer.Finalize(
            path,
            new Dictionary<string, ResolvedStyle>
            {
                [StyleRoles.InlineCode] = new("ExampleBody", "示例 正文"),
            },
            bodyOnly: true);

        using var document = WordprocessingDocument.Open(path, false);
        var inlineRun = document.MainDocumentPart!.Document!.Body!
            .Descendants<Run>()
            .Single(run => run.InnerText == "inline_code()");
        Assert.Equal("SimSun", inlineRun.RunProperties!.RunFonts!.Ascii!.Value);
        Assert.Equal("24", inlineRun.RunProperties.FontSize!.Val!.Value);
        Assert.Null(inlineRun.RunProperties.RunStyle);
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    private static void CreateDocument(string path, string paragraphStyleId)
    {
        const string id = "0123456789abcdef0123456789abcdef";
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            new Style(new StyleName { Val = "示例 正文" }, new StyleRunProperties(
                new RunFonts { Ascii = "SimSun", HighAnsi = "SimSun", EastAsia = "宋体" },
                new FontSize { Val = "24" }))
            {
                Type = StyleValues.Paragraph,
                StyleId = "ExampleBody",
            },
            new Style(new StyleName { Val = "Heading 1" })
            {
                Type = StyleValues.Paragraph,
                StyleId = "Heading1",
            },
            new Style(new StyleName { Val = "HTML Code" })
            {
                Type = StyleValues.Character,
                StyleId = "HTMLCode",
            });
        stylesPart.Styles.Save();

        var paragraph = new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = paragraphStyleId }),
            new Run(new Text("before ") { Space = SpaceProcessingModeValues.Preserve }),
            HtmlCodeRun("__MD2WORD_INLINE_CODE_STA"),
            HtmlCodeRun($"RT_{id}__inline_code()__MD2WORD_INLINE_CODE_END_{id}__"),
            new Run(new RunProperties(new Italic()), new Text(" after") { Space = SpaceProcessingModeValues.Preserve }));
        mainPart.Document = new Document(new Body(
            new Paragraph(
                new BookmarkStart { Name = TemplateValidationService.BodyStartBookmark, Id = "1" },
                new BookmarkEnd { Id = "1" }),
            paragraph,
            new Paragraph(
                new BookmarkStart { Name = TemplateValidationService.BodyEndBookmark, Id = "2" },
                new BookmarkEnd { Id = "2" })));
        mainPart.Document.Save();
    }

    private static Run HtmlCodeRun(string text) => new(
        new RunProperties(
            new RunStyle { Val = "HTMLCode" },
            new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New" },
            new Shading { Fill = "E7E6E6" }),
        new Text(text) { Space = SpaceProcessingModeValues.Preserve });
}
