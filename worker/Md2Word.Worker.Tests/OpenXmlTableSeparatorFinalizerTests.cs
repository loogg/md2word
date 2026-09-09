using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlTableSeparatorFinalizerTests
{
    private const string BodyStyleId = "ExampleBody";

    [Fact]
    public void UsesExistingCssMappedBodyStyleForSafeAdjacentTableSeparators()
    {
        using var workspace = new SyntheticWorkspace();
        var path = CreateDocument(workspace);

        var result = OpenXmlTableSeparatorFinalizer.Finalize(
            path,
            new Dictionary<string, ResolvedStyle>
            {
                [StyleRoles.Body] = new(BodyStyleId, "示例 正文"),
            },
            bodyOnly: true);

        Assert.Equal(2, result.SeparatorsStyled);
        Assert.Equal(1, result.HiddenSeparatorsRestored);
        using var document = WordprocessingDocument.Open(path, false);
        var mainPart = document.MainDocumentPart!;
        var body = mainPart.Document!.Body!;
        var separators = new[]
        {
            Assert.IsType<Paragraph>(body.ChildElements[5]),
            Assert.IsType<Paragraph>(body.ChildElements[7]),
        };
        Assert.All(separators, paragraph =>
        {
            var properties = paragraph.ParagraphProperties!;
            Assert.Equal(BodyStyleId, properties.ParagraphStyleId!.Val!.Value);
            Assert.Single(properties.ChildElements);
            Assert.Empty(paragraph.Descendants<Vanish>());
            Assert.Empty(paragraph.Elements<Run>());
        });

        AssertUnchanged(Assert.IsType<Paragraph>(body.ChildElements[1]));
        AssertUnchanged(Assert.IsType<Paragraph>(body.ChildElements[13]));
        AssertUnchanged(Assert.IsType<Paragraph>(body.ChildElements[9]));
        var styles = mainPart.StyleDefinitionsPart!.Styles!.Elements<Style>().ToArray();
        Assert.Single(styles);
        Assert.Equal(BodyStyleId, styles[0].StyleId!.Value);
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    private static string CreateDocument(SyntheticWorkspace workspace)
    {
        var path = workspace.PathFor("table-separators.docx");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = BodyStyleId,
            CustomStyle = true,
            StyleName = new StyleName { Val = "示例 正文" },
        });
        stylesPart.Styles.Save();

        mainPart.Document = new Document(new Body(
            Table("outside-a"),
            HiddenSeparator("outside-before"),
            Table("outside-b"),
            BookmarkParagraph(TemplateValidationService.BodyStartBookmark, "1"),
            Table("inside-a"),
            HiddenSeparator("inside-hidden"),
            Table("inside-b"),
            EmptySeparator("inside-empty"),
            Table("inside-c"),
            SemanticSeparator("inside-semantic"),
            Table("inside-d"),
            BookmarkParagraph(TemplateValidationService.BodyEndBookmark, "2"),
            Table("outside-c"),
            HiddenSeparator("outside-after"),
            Table("outside-d"),
            new SectionProperties()));
        mainPart.Document.Save();
        return path;
    }

    private static Paragraph HiddenSeparator(string _) => new(
        new ParagraphProperties(
            new SpacingBetweenLines { Before = "240", After = "240" },
            new Justification { Val = JustificationValues.Center },
            new ParagraphMarkRunProperties(new Vanish())));

    private static Paragraph EmptySeparator(string _) => new(
        new ParagraphProperties(
            new ParagraphStyleId { Val = "ImporterStyle" },
            new Indentation { Left = "720" }),
        new Run(new RunProperties(new Italic()), new Text(string.Empty)));

    private static Paragraph SemanticSeparator(string _) => new(
        new ParagraphProperties(new ParagraphMarkRunProperties(new Vanish())),
        new Run(new Break()));

    private static void AssertUnchanged(Paragraph paragraph)
    {
        Assert.Null(paragraph.ParagraphProperties?.ParagraphStyleId);
        Assert.NotEmpty(paragraph.Descendants<Vanish>());
    }

    private static Table Table(string text) => new(
        new TableProperties(),
        new TableGrid(new GridColumn { Width = "2000" }),
        new TableRow(new TableCell(new Paragraph(new Run(new Text(text))))));

    private static Paragraph BookmarkParagraph(string name, string id) => new(
        new BookmarkStart { Name = name, Id = id },
        new BookmarkEnd { Id = id });
}
