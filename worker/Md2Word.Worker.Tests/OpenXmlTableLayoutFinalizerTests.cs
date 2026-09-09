using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlTableLayoutFinalizerTests
{
    [Fact]
    public void AppliesAuditedTwoColumnWidthIntentInsideTemplateBodyBookmarks()
    {
        using var workspace = new SyntheticWorkspace();
        var path = CreateDocument(workspace);

        var result = OpenXmlTableLayoutFinalizer.Finalize(path, bodyOnly: true);

        Assert.Equal(1, result.TablesVisited);
        Assert.Equal(1, result.TablesSized);
        Assert.Equal(0, result.MergedTablesSized);
        Assert.Equal(0, result.TablesSkippedForIrregularGrid);
        using var document = WordprocessingDocument.Open(path, false);
        var tables = document.MainDocumentPart!.Document!.Body!.Elements<Table>().ToArray();
        AssertTableIsUnchanged(tables[0]);
        AssertTableHasFixedWidths(tables[1], total: 10_000, first: 2_400, second: 7_600);
        AssertTableIsUnchanged(tables[2]);
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    [Fact]
    public void FinalizesRegularRowspanAndColspanTablesWithoutDestroyingMergeMarkup()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("merged-table.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(CreateMergedTable(), Section()));
            mainPart.Document.Save();
        }

        var result = OpenXmlTableLayoutFinalizer.Finalize(path, bodyOnly: false);

        Assert.Equal(1, result.TablesVisited);
        Assert.Equal(1, result.TablesSized);
        Assert.Equal(1, result.MergedTablesSized);
        Assert.Equal(0, result.TablesSkippedForIrregularGrid);
        using var finalized = WordprocessingDocument.Open(path, false);
        var table = Assert.Single(finalized.MainDocumentPart!.Document!.Body!.Elements<Table>());
        AssertMergedTablePresentation(table, total: 10_000);
        Assert.Empty(new OpenXmlValidator().Validate(finalized));
    }

    [Fact]
    public void SkipsIrregularMergedGridWithoutChangingImportedLayout()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("irregular-merged-table.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();
            var irregular = CreateTable("irregular");
            irregular.Elements<TableRow>().First().Elements<TableCell>().First()
                .TableCellProperties!.Append(new GridSpan { Val = 2 });
            mainPart.Document = new Document(new Body(irregular, Section()));
            mainPart.Document.Save();
        }

        var result = OpenXmlTableLayoutFinalizer.Finalize(path, bodyOnly: false);

        Assert.Equal(1, result.TablesVisited);
        Assert.Equal(0, result.TablesSized);
        Assert.Equal(0, result.MergedTablesSized);
        Assert.Equal(1, result.TablesSkippedForIrregularGrid);
    }

    private static string CreateDocument(SyntheticWorkspace workspace)
    {
        var path = workspace.PathFor("table-layout.docx");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(
            CreateTable("outside-before"),
            BookmarkParagraph(TemplateValidationService.BodyStartBookmark, "1"),
            CreateTable("inside"),
            BookmarkParagraph(TemplateValidationService.BodyEndBookmark, "2"),
            CreateTable("outside-after"),
            Section()));
        mainPart.Document.Save();
        return path;
    }

    private static Table CreateTable(string prefix)
    {
        var table = new Table(
            new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Auto, Width = "0" },
                new TableCellSpacing { Type = TableWidthUnitValues.Dxa, Width = "15" }),
            new TableGrid(new GridColumn { Width = "4000" }, new GridColumn { Width = "4000" }));
        for (var rowIndex = 0; rowIndex < 2; rowIndex++)
        {
            table.Append(new TableRow(
                new TableRowProperties(new TableCellSpacing { Type = TableWidthUnitValues.Dxa, Width = "15" }),
                Cell($"{prefix}-key-{rowIndex}"),
                Cell($"{prefix}-value-{rowIndex}")));
        }
        return table;
    }

    private static Table CreateMergedTable()
    {
        var table = new Table(
            new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Auto, Width = "0" },
                new TableCellSpacing { Type = TableWidthUnitValues.Dxa, Width = "15" }),
            new TableGrid(
                new GridColumn { Width = "1000" },
                new GridColumn { Width = "1500" },
                new GridColumn { Width = "1500" },
                new GridColumn { Width = "6000" }),
            new TableRow(
                new TableRowProperties(new TableCellSpacing { Type = TableWidthUnitValues.Dxa, Width = "15" }),
                Cell("Group"),
                Cell("Byte"),
                Cell("Name"),
                Cell("Description")),
            new TableRow(
                new TableRowProperties(new TableCellSpacing { Type = TableWidthUnitValues.Dxa, Width = "15" }),
                MergedCell("Header", restart: true),
                Cell("1 ~ 4"),
                SpannedCell("Combined description", span: 2)),
            new TableRow(
                new TableRowProperties(new TableCellSpacing { Type = TableWidthUnitValues.Dxa, Width = "15" }),
                MergedCell(string.Empty, restart: false),
                Cell("5 ~ 8"),
                Cell("Type"),
                Cell("Details")));
        return table;
    }

    private static TableCell Cell(string text) => new(
        new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Auto, Width = "0" }),
        new Paragraph(new Run(new Text(text))));

    private static TableCell MergedCell(string text, bool restart) => new(
        new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Auto, Width = "0" },
            restart
                ? new VerticalMerge { Val = MergedCellValues.Restart }
                : new VerticalMerge()),
        new Paragraph(new Run(new Text(text))));

    private static TableCell SpannedCell(string text, int span) => new(
        new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Auto, Width = "0" },
            new GridSpan { Val = span }),
        new Paragraph(new Run(new Text(text))));

    private static SectionProperties Section() => new(
        new PageSize { Width = 12_000, Height = 16_000 },
        new PageMargin { Left = 1_000, Right = 1_000, Top = 1_000, Bottom = 1_000 });

    private static Paragraph BookmarkParagraph(string name, string id) => new(
        new BookmarkStart { Name = name, Id = id },
        new BookmarkEnd { Id = id });

    private static void AssertTableHasFixedWidths(Table table, int total, int first, int second)
    {
        var properties = table.TableProperties!;
        Assert.Equal(TableWidthUnitValues.Dxa, properties.GetFirstChild<TableWidth>()!.Type!.Value);
        Assert.Equal(total.ToString(), properties.GetFirstChild<TableWidth>()!.Width!.Value);
        Assert.Equal(TableLayoutValues.Fixed, properties.GetFirstChild<TableLayout>()!.Type!.Value);
        Assert.Equal(
            [first.ToString(), second.ToString()],
            table.TableGrid!.Elements<GridColumn>().Select(column => column.Width!.Value));
        Assert.Equal("0", properties.GetFirstChild<TableIndentation>()!.Width!.Value.ToString());
        Assert.Equal(TableWidthUnitValues.Dxa, properties.GetFirstChild<TableIndentation>()!.Type!.Value);
        Assert.Equal(TableRowAlignmentValues.Left, properties.GetFirstChild<TableJustification>()!.Val!.Value);
        Assert.Null(properties.GetFirstChild<TableCellSpacing>());

        var borders = properties.GetFirstChild<TableBorders>()!;
        Assert.Collection(
            borders.ChildElements.Cast<BorderType>(),
            AssertVisibleBorder,
            AssertVisibleBorder,
            AssertVisibleBorder,
            AssertVisibleBorder,
            AssertVisibleBorder,
            AssertVisibleBorder);
        var margins = properties.GetFirstChild<TableCellMarginDefault>()!;
        Assert.Equal("80", margins.GetFirstChild<TopMargin>()!.Width!.Value);
        Assert.Equal(120, margins.GetFirstChild<TableCellLeftMargin>()!.Width!.Value);
        Assert.Equal("80", margins.GetFirstChild<BottomMargin>()!.Width!.Value);
        Assert.Equal(120, margins.GetFirstChild<TableCellRightMargin>()!.Width!.Value);

        Assert.All(table.Elements<TableRow>(), row => Assert.Equal(
            [first.ToString(), second.ToString()],
            row.Elements<TableCell>().Select(cell => cell.TableCellProperties!.TableCellWidth!.Width!.Value)));
        var rows = table.Elements<TableRow>().ToArray();
        Assert.All(rows, row => Assert.Null(row.TableRowProperties?.GetFirstChild<TableCellSpacing>()));
        Assert.All(rows.SelectMany(row => row.Elements<TableCell>()), cell =>
            Assert.Equal(
                TableVerticalAlignmentValues.Center,
                cell.TableCellProperties!.TableCellVerticalAlignment!.Val!.Value));
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            foreach (var paragraph in rows[rowIndex].Descendants<Paragraph>())
            {
                var paragraphProperties = paragraph.ParagraphProperties!;
                Assert.Equal("0", paragraphProperties.Indentation!.FirstLine!.Value);
                Assert.Equal("0", paragraphProperties.SpacingBetweenLines!.Before!.Value);
                Assert.Equal("0", paragraphProperties.SpacingBetweenLines.After!.Value);
                Assert.Equal("240", paragraphProperties.SpacingBetweenLines.Line!.Value);
                Assert.Equal(
                    rowIndex == 0 ? JustificationValues.Center : JustificationValues.Left,
                    paragraphProperties.Justification!.Val!.Value);
                Assert.All(paragraph.Descendants<Run>(), run =>
                {
                    if (rowIndex == 0)
                    {
                        Assert.NotNull(run.RunProperties?.Bold);
                        Assert.NotNull(run.RunProperties?.BoldComplexScript);
                    }
                    else
                    {
                        Assert.Null(run.RunProperties?.Bold);
                    }
                });
            }
        }
    }

    private static void AssertVisibleBorder(BorderType border)
    {
        Assert.Equal(BorderValues.Single, border.Val!.Value);
        Assert.Equal("000000", border.Color!.Value);
        Assert.Equal(4U, border.Size!.Value);
    }

    private static void AssertMergedTablePresentation(Table table, int total)
    {
        var properties = table.TableProperties!;
        Assert.Equal(total.ToString(), properties.GetFirstChild<TableWidth>()!.Width!.Value);
        Assert.Equal(TableWidthUnitValues.Dxa, properties.GetFirstChild<TableWidth>()!.Type!.Value);
        Assert.Equal(TableLayoutValues.Autofit, properties.GetFirstChild<TableLayout>()!.Type!.Value);
        Assert.Equal(
            ["1000", "1500", "1500", "6000"],
            table.TableGrid!.Elements<GridColumn>().Select(column => column.Width!.Value));
        Assert.Null(properties.GetFirstChild<TableCellSpacing>());
        Assert.Equal("0", properties.GetFirstChild<TableIndentation>()!.Width!.Value.ToString());
        Assert.Equal(TableRowAlignmentValues.Left, properties.GetFirstChild<TableJustification>()!.Val!.Value);
        Assert.Equal(6, properties.GetFirstChild<TableBorders>()!.ChildElements.Count);

        var rows = table.Elements<TableRow>().ToArray();
        Assert.All(rows, row => Assert.Null(row.TableRowProperties?.GetFirstChild<TableCellSpacing>()));
        Assert.All(rows.SelectMany(row => row.Elements<TableCell>()), cell =>
        {
            Assert.Equal(TableWidthUnitValues.Auto, cell.TableCellProperties!.TableCellWidth!.Type!.Value);
            Assert.Equal(
                TableVerticalAlignmentValues.Center,
                cell.TableCellProperties.TableCellVerticalAlignment!.Val!.Value);
        });
        Assert.Equal(
            MergedCellValues.Restart,
            rows[1].Elements<TableCell>().First().TableCellProperties!.VerticalMerge!.Val!.Value);
        Assert.Null(rows[2].Elements<TableCell>().First().TableCellProperties!.VerticalMerge!.Val);
        Assert.Equal(
            2,
            rows[1].Elements<TableCell>().ElementAt(2).TableCellProperties!.GridSpan!.Val!.Value);
        Assert.All(rows[0].Descendants<Run>(), run => Assert.NotNull(run.RunProperties?.Bold));
    }

    private static void AssertTableIsUnchanged(Table table)
    {
        Assert.Equal(TableWidthUnitValues.Auto, table.TableProperties!.GetFirstChild<TableWidth>()!.Type!.Value);
        Assert.Null(table.TableProperties.GetFirstChild<TableLayout>());
    }
}
