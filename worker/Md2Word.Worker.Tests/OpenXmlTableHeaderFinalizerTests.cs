using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlTableHeaderFinalizerTests
{
    [Fact]
    public void EnablesTheFirstRowOnlyForTablesInsideTemplateBodyBookmarks()
    {
        using var workspace = new SyntheticWorkspace();
        var path = CreateDocument(workspace, includeHeaders: true);

        var result = OpenXmlTableHeaderFinalizer.Finalize(path, enabled: true, bodyOnly: true);

        Assert.Equal(1, result.TablesVisited);
        Assert.Equal(1, result.HeaderRowsEnabled);
        Assert.Equal(2, result.HeaderFlagsRemoved);
        using var document = WordprocessingDocument.Open(path, false);
        var tables = document.MainDocumentPart!.Document!.Body!.Elements<Table>().ToArray();
        Assert.Equal(3, tables.Length);
        Assert.True(IsHeader(tables[0].Elements<TableRow>().First()));
        Assert.True(IsHeader(tables[1].Elements<TableRow>().First()));
        Assert.False(IsHeader(tables[1].Elements<TableRow>().Skip(1).First()));
        Assert.True(IsHeader(tables[2].Elements<TableRow>().First()));
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    [Fact]
    public void DisablesEveryHeaderFlagOnlyInsideTemplateBodyBookmarks()
    {
        using var workspace = new SyntheticWorkspace();
        var path = CreateDocument(workspace, includeHeaders: true);

        var result = OpenXmlTableHeaderFinalizer.Finalize(path, enabled: false, bodyOnly: true);

        Assert.Equal(1, result.TablesVisited);
        Assert.Equal(0, result.HeaderRowsEnabled);
        Assert.Equal(2, result.HeaderFlagsRemoved);
        using var document = WordprocessingDocument.Open(path, false);
        var tables = document.MainDocumentPart!.Document!.Body!.Elements<Table>().ToArray();
        Assert.True(IsHeader(tables[0].Elements<TableRow>().First()));
        Assert.All(tables[1].Elements<TableRow>(), row => Assert.False(IsHeader(row)));
        Assert.True(IsHeader(tables[2].Elements<TableRow>().First()));
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    private static string CreateDocument(SyntheticWorkspace workspace, bool includeHeaders)
    {
        var path = workspace.PathFor("repeat-headers.docx");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var body = new Body(
            CreateTable("outside-before", includeHeaders ? [true] : [false]),
            BookmarkParagraph(TemplateValidationService.BodyStartBookmark, "1"),
            CreateTable("inside", includeHeaders ? [true, true] : [false, false]),
            BookmarkParagraph(TemplateValidationService.BodyEndBookmark, "2"),
            CreateTable("outside-after", includeHeaders ? [true] : [false]));
        mainPart.Document = new Document(body);
        mainPart.Document.Save();
        return path;
    }

    private static Table CreateTable(string prefix, IReadOnlyList<bool> headerRows)
    {
        var table = new Table(new TableProperties(new TableBorders(
            new TopBorder { Val = BorderValues.Single },
            new LeftBorder { Val = BorderValues.Single },
            new BottomBorder { Val = BorderValues.Single },
            new RightBorder { Val = BorderValues.Single },
            new InsideHorizontalBorder { Val = BorderValues.Single },
            new InsideVerticalBorder { Val = BorderValues.Single })),
            new TableGrid(new GridColumn { Width = "4000" }));
        for (var index = 0; index < headerRows.Count; index++)
        {
            var row = new TableRow();
            if (headerRows[index])
            {
                row.Append(new TableRowProperties(new TableHeader()));
            }
            row.Append(new TableCell(new Paragraph(new Run(new Text($"{prefix}-{index + 1}")))));
            table.Append(row);
        }
        return table;
    }

    private static Paragraph BookmarkParagraph(string name, string id) => new(
        new BookmarkStart { Name = name, Id = id },
        new BookmarkEnd { Id = id });

    private static bool IsHeader(TableRow row) =>
        row.TableRowProperties?.Elements<TableHeader>().Any() == true;
}
