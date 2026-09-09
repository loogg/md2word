using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlTemplateMetadataFinalizerTests
{
    [Fact]
    public void ReplacesCoverFieldsAndRebuildsVersionRowsWithoutChangingTheHeader()
    {
        using var workspace = new SyntheticWorkspace();
        var path = CreateMetadataDocument(workspace);
        var metadata = new PandocMetadata(
            "New synthetic title",
            "New synthetic subtitle",
            [new VersionTableMetadata(
                "MANUAL_TABLE_VERSION_HISTORY",
                ["version", "description"],
                [
                    new Dictionary<string, string>
                    {
                        ["version"] = "1.0",
                        ["description"] = "First line\nSecond line",
                    },
                    new Dictionary<string, string>
                    {
                        ["version"] = "1.1",
                        ["description"] = "Follow-up",
                    },
                ])]);

        var result = new OpenXmlTemplateMetadataFinalizer().Finalize(path, metadata);

        Assert.Equal(2, result.CoverFieldsUpdated);
        Assert.Equal(1, result.VersionTablesUpdated);
        Assert.Equal(2, result.VersionRowsWritten);

        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        var titleParagraph = body.Elements<Paragraph>().Single(paragraph => paragraph.InnerText.Contains("New synthetic title"));
        Assert.Equal("prefix-New synthetic title-suffix", titleParagraph.InnerText);
        Assert.Contains(titleParagraph.Elements<BookmarkStart>(), bookmark => bookmark.Name == TemplateValidationService.CoverTitleBookmark);
        Assert.Contains(titleParagraph.Elements<BookmarkEnd>(), bookmark => bookmark.Id == "10");

        var subtitleParagraph = body.Elements<Paragraph>().Single(paragraph => paragraph.InnerText.Contains("New synthetic subtitle"));
        Assert.Equal("New synthetic subtitle", subtitleParagraph.InnerText);
        Assert.Contains(subtitleParagraph.Elements<BookmarkStart>(), bookmark => bookmark.Name == TemplateValidationService.CoverSubtitleBookmark);

        var table = Assert.Single(body.Elements<Table>());
        var rows = table.Elements<TableRow>().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal("VersionDescription", rows[0].InnerText);
        Assert.Equal("1.0", rows[1].Elements<TableCell>().ElementAt(0).InnerText);
        Assert.Equal("First lineSecond line", rows[1].Elements<TableCell>().ElementAt(1).InnerText);
        Assert.Single(rows[1].Elements<TableCell>().ElementAt(1).Descendants<Break>());
        Assert.Equal("1.1Follow-up", rows[2].InnerText);
        Assert.Empty(rows[1].Descendants<TableHeader>());

        var tableBookmark = body.Elements<BookmarkStart>()
            .Single(bookmark => bookmark.Name == "MANUAL_TABLE_VERSION_HISTORY");
        var tableBookmarkEnd = body.Elements<BookmarkEnd>().Single(bookmark => bookmark.Id == tableBookmark.Id);
        Assert.Same(table, tableBookmark.NextSibling<Table>());
        Assert.Same(table, tableBookmarkEnd.PreviousSibling<Table>());
        var validationErrors = new OpenXmlValidator().Validate(document).ToArray();
        Assert.True(
            validationErrors.Length == 0,
            string.Join(Environment.NewLine, validationErrors.Select(error => $"{error.Description} :: {error.Node?.OuterXml}")));
    }

    [Fact]
    public void EmptyMetadataIsANoOpAndDoesNotRequireOptionalBookmarks()
    {
        var result = new OpenXmlTemplateMetadataFinalizer().Finalize(
            "the file is intentionally not opened",
            new PandocMetadata(null, null, []));

        Assert.Equal(new TemplateMetadataFinalizationResult(0, 0, 0), result);
    }

    [Theory]
    [InlineData("title", "FRONT_MATTER_TITLE_BOOKMARK_MISSING")]
    [InlineData("subtitle", "FRONT_MATTER_SUBTITLE_BOOKMARK_MISSING")]
    [InlineData("version", "FRONT_MATTER_VERSION_TABLE_BOOKMARK_MISSING")]
    public void ExplicitMissingCapabilityFailsWithStableCode(string field, string expectedCode)
    {
        var metadata = field switch
        {
            "title" => new PandocMetadata("Explicit", null, []),
            "subtitle" => new PandocMetadata(null, "Explicit", []),
            _ => new PandocMetadata(null, null,
                [new VersionTableMetadata("MANUAL_TABLE_VERSION_HISTORY", ["version"], [])]),
        };

        var exception = Assert.Throws<WorkerCommandException>(() =>
            OpenXmlTemplateMetadataFinalizer.EnsureCapabilities(
                new TemplateCapabilities(true, false, false, [], true),
                metadata));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal("openxml-finalize", exception.Stage);
    }

    [Fact]
    public void VersionBookmarkMustContainExactlyOneTable()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("invalid-version-range.docx");
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = package.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                new BookmarkStart { Name = "MANUAL_TABLE_VERSION_HISTORY", Id = "30" },
                SimpleTable("Header A", "old A"),
                SimpleTable("Header B", "old B"),
                new BookmarkEnd { Id = "30" }));
            mainPart.Document.Save();
        }
        var metadata = new PandocMetadata(
            null,
            null,
            [new VersionTableMetadata("MANUAL_TABLE_VERSION_HISTORY", ["only"], [])]);

        var exception = Assert.Throws<WorkerCommandException>(() =>
            new OpenXmlTemplateMetadataFinalizer().Finalize(path, metadata));

        Assert.Equal("FRONT_MATTER_VERSION_TABLE_BOOKMARK_RANGE_INVALID", exception.Code);
    }

    private static string CreateMetadataDocument(SyntheticWorkspace workspace)
    {
        var path = workspace.PathFor("metadata.docx");
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = package.AddMainDocumentPart();
        var title = new Paragraph(
            new Run(new Text("prefix-")),
            new BookmarkStart { Name = TemplateValidationService.CoverTitleBookmark, Id = "10" },
            new Run(new Text("old title")),
            new BookmarkEnd { Id = "10" },
            new Run(new Text("-suffix")));
        var subtitle = new Paragraph(
            new BookmarkStart { Name = TemplateValidationService.CoverSubtitleBookmark, Id = "11" },
            new Run(new Text("old subtitle")),
            new BookmarkEnd { Id = "11" });
        var versionTable = new Table(
            new TableProperties(new TableWidth { Type = TableWidthUnitValues.Auto, Width = "0" }),
            new TableGrid(new GridColumn { Width = "1800" }, new GridColumn { Width = "5400" }),
            TableRow("Version", "Description", header: true),
            TableRow("old", "old description"),
            TableRow("stale", "stale description"));
        mainPart.Document = new Document(new Body(
            title,
            subtitle,
            new BookmarkStart { Name = "MANUAL_TABLE_VERSION_HISTORY", Id = "30" },
            versionTable,
            new BookmarkEnd { Id = "30" },
            new Paragraph(new Run(new Text("body remains synthetic")))));
        mainPart.Document.Save();
        return path;
    }

    private static Table SimpleTable(string header, string value) => new(
        new TableProperties(new TableWidth { Type = TableWidthUnitValues.Auto, Width = "0" }),
        new TableGrid(new GridColumn { Width = "3600" }),
        TableRow(header, header: true),
        TableRow(value));

    private static TableRow TableRow(string first, string? second = null, bool header = false)
    {
        var row = new TableRow();
        if (header)
        {
            row.Append(new TableRowProperties(new TableHeader()));
        }
        row.Append(TableCell(first));
        if (second is not null)
        {
            row.Append(TableCell(second));
        }
        return row;
    }

    private static TableCell TableCell(string value) => new(
        new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "2400" }),
        new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Left }),
            new Run(
                new RunProperties(new FontSize { Val = "20" }),
                new Text(value))));
}
