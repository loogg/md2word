using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal sealed record TemplateMetadataFinalizationResult(
    int CoverFieldsUpdated,
    int VersionTablesUpdated,
    int VersionRowsWritten);

internal sealed class OpenXmlTemplateMetadataFinalizer
{
    public static void EnsureCapabilities(TemplateCapabilities capabilities, PandocMetadata metadata)
    {
        if (metadata.Title is not null && !capabilities.CoverTitle)
        {
            throw Error(
                "FRONT_MATTER_TITLE_BOOKMARK_MISSING",
                $"Explicit title metadata requires the {TemplateValidationService.CoverTitleBookmark} bookmark.");
        }
        if (metadata.Subtitle is not null && !capabilities.CoverSubtitle)
        {
            throw Error(
                "FRONT_MATTER_SUBTITLE_BOOKMARK_MISSING",
                $"Explicit subtitle metadata requires the {TemplateValidationService.CoverSubtitleBookmark} bookmark.");
        }

        var availableTables = new HashSet<string>(capabilities.VersionTables, StringComparer.Ordinal);
        if (metadata.VersionTables.Any(table => !availableTables.Contains(table.Bookmark)))
        {
            throw Error(
                "FRONT_MATTER_VERSION_TABLE_BOOKMARK_MISSING",
                "Explicit version-table metadata requires its named template bookmark.");
        }
    }

    public TemplateMetadataFinalizationResult Finalize(string docxPath, PandocMetadata metadata)
    {
        if (!metadata.HasExplicitValues)
        {
            return new TemplateMetadataFinalizationResult(0, 0, 0);
        }

        try
        {
            using var document = WordprocessingDocument.Open(docxPath, true);
            var mainPart = document.MainDocumentPart
                ?? throw Error("TEMPLATE_METADATA_DOCUMENT_INVALID", "The generated DOCX has no main document part.");
            var root = mainPart.Document
                ?? throw Error("TEMPLATE_METADATA_DOCUMENT_INVALID", "The generated DOCX has no main document.");

            var coverFields = 0;
            if (metadata.Title is not null)
            {
                ReplaceBookmarkedText(root, TemplateValidationService.CoverTitleBookmark, metadata.Title, "TITLE");
                coverFields++;
            }
            if (metadata.Subtitle is not null)
            {
                ReplaceBookmarkedText(root, TemplateValidationService.CoverSubtitleBookmark, metadata.Subtitle, "SUBTITLE");
                coverFields++;
            }

            var rowsWritten = 0;
            foreach (var versionTable in metadata.VersionTables)
            {
                rowsWritten += ReplaceVersionTable(root, versionTable);
            }

            root.Save();
            return new TemplateMetadataFinalizationResult(
                coverFields,
                metadata.VersionTables.Count,
                rowsWritten);
        }
        catch (WorkerCommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OpenXmlPackageException)
        {
            throw new WorkerCommandException(
                "TEMPLATE_METADATA_UPDATE_FAILED",
                "The template metadata could not be written to the generated DOCX.",
                4,
                "openxml-finalize",
                inner: exception);
        }
    }

    private static void ReplaceBookmarkedText(
        Document root,
        string bookmarkName,
        string value,
        string fieldName)
    {
        var range = FindBookmark(root, bookmarkName, $"FRONT_MATTER_{fieldName}_BOOKMARK_MISSING");
        if (range.Start.Parent is not Paragraph paragraph
            || !ReferenceEquals(range.End.Parent, paragraph))
        {
            throw Error(
                $"FRONT_MATTER_{fieldName}_BOOKMARK_RANGE_INVALID",
                $"The {bookmarkName} bookmark must wrap text within one paragraph.");
        }

        var children = paragraph.ChildElements.ToList();
        var startIndex = children.IndexOf(range.Start);
        var endIndex = children.IndexOf(range.End);
        if (startIndex < 0 || endIndex <= startIndex)
        {
            throw Error(
                $"FRONT_MATTER_{fieldName}_BOOKMARK_RANGE_INVALID",
                $"The {bookmarkName} bookmark range is invalid.");
        }
        if (children.Skip(startIndex + 1).Take(endIndex - startIndex - 1)
            .Any(element => element is BookmarkStart or BookmarkEnd))
        {
            throw Error(
                $"FRONT_MATTER_{fieldName}_BOOKMARK_RANGE_INVALID",
                $"The {bookmarkName} bookmark must not contain nested bookmarks.");
        }

        var oldRange = children.Skip(startIndex).Take(endIndex - startIndex + 1).ToArray();
        var recreatedStart = (BookmarkStart)range.Start.CloneNode(true);
        var recreatedEnd = (BookmarkEnd)range.End.CloneNode(true);
        paragraph.InsertBefore(recreatedStart, range.Start);
        paragraph.InsertBefore(CreateTextRun(value), range.Start);
        paragraph.InsertBefore(recreatedEnd, range.Start);
        foreach (var element in oldRange)
        {
            element.Remove();
        }
    }

    private static int ReplaceVersionTable(Document root, VersionTableMetadata metadata)
    {
        var range = FindBookmark(
            root,
            metadata.Bookmark,
            "FRONT_MATTER_VERSION_TABLE_BOOKMARK_MISSING");
        var table = FindExactlyOneTable(root, range, metadata.Bookmark);
        var tableRows = table.Elements<TableRow>().ToList();
        if (tableRows.Count == 0)
        {
            throw Error(
                "FRONT_MATTER_VERSION_TABLE_LAYOUT_INVALID",
                "A version-table bookmark must contain a table with one header row.");
        }

        var headerCells = tableRows[0].Elements<TableCell>().ToList();
        if (headerCells.Count == 0 || metadata.ColumnKeys.Count > headerCells.Count)
        {
            throw Error(
                "FRONT_MATTER_VERSION_TABLE_COLUMN_COUNT_MISMATCH",
                "The version table has fewer template columns than column_keys entries.");
        }

        var dataRowTemplate = (TableRow)(tableRows.Count > 1
            ? tableRows[1].CloneNode(true)
            : tableRows[0].CloneNode(true));
        var templateCells = dataRowTemplate.Elements<TableCell>().ToList();
        if (templateCells.Count != headerCells.Count)
        {
            throw Error(
                "FRONT_MATTER_VERSION_TABLE_LAYOUT_INVALID",
                "The version table data-row layout must match its header columns.");
        }

        foreach (var row in tableRows.Skip(1))
        {
            row.Remove();
        }

        foreach (var rowMetadata in metadata.Rows)
        {
            var row = (TableRow)dataRowTemplate.CloneNode(true);
            row.TableRowProperties?.RemoveAllChildren<TableHeader>();
            var cells = row.Elements<TableCell>().ToArray();
            for (var index = 0; index < cells.Length; index++)
            {
                var value = index < metadata.ColumnKeys.Count
                    && rowMetadata.TryGetValue(metadata.ColumnKeys[index], out var configuredValue)
                        ? configuredValue
                        : string.Empty;
                ReplaceCellText(cells[index], value);
            }
            table.Append(row);
        }

        RebindBookmarkToTable(range, table, metadata.Bookmark);
        return metadata.Rows.Count;
    }

    private static void ReplaceCellText(TableCell cell, string value)
    {
        var templateParagraph = cell.Elements<Paragraph>().FirstOrDefault();
        var paragraphProperties = (ParagraphProperties?)templateParagraph?.ParagraphProperties?.CloneNode(true);
        var runProperties = (RunProperties?)templateParagraph?.Descendants<RunProperties>().FirstOrDefault()?.CloneNode(true);

        foreach (var child in cell.ChildElements.Where(element => element is not TableCellProperties).ToArray())
        {
            child.Remove();
        }

        var paragraph = new Paragraph();
        if (paragraphProperties is not null)
        {
            paragraph.Append(paragraphProperties);
        }
        var run = CreateTextRun(value, runProperties);
        paragraph.Append(run);
        cell.Append(paragraph);
    }

    private static Run CreateTextRun(string value, RunProperties? runProperties = null)
    {
        var run = new Run();
        if (runProperties is not null)
        {
            run.Append(runProperties);
        }

        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                run.Append(new Break());
            }
            run.Append(new Text(lines[index]) { Space = SpaceProcessingModeValues.Preserve });
        }
        return run;
    }

    private static BookmarkRange FindBookmark(Document root, string name, string missingCode)
    {
        var starts = root.Descendants<BookmarkStart>()
            .Where(bookmark => string.Equals(bookmark.Name?.Value, name, StringComparison.Ordinal))
            .ToArray();
        if (starts.Length == 0)
        {
            throw Error(missingCode, $"The explicit metadata requires the {name} bookmark.");
        }
        if (starts.Length > 1 || string.IsNullOrWhiteSpace(starts[0].Id?.Value))
        {
            throw Error("FRONT_MATTER_BOOKMARK_AMBIGUOUS", "An explicit metadata bookmark is duplicated or has no id.");
        }

        var id = starts[0].Id!.Value!;
        var ends = root.Descendants<BookmarkEnd>()
            .Where(bookmark => string.Equals(bookmark.Id?.Value, id, StringComparison.Ordinal))
            .ToArray();
        if (ends.Length != 1)
        {
            throw Error("FRONT_MATTER_BOOKMARK_RANGE_INVALID", "An explicit metadata bookmark has no unique end marker.");
        }
        return new BookmarkRange(starts[0], ends[0]);
    }

    private static Table FindExactlyOneTable(Document root, BookmarkRange range, string bookmarkName)
    {
        var documentOrder = root.Descendants().ToList();
        var startIndex = documentOrder.IndexOf(range.Start);
        var endIndex = documentOrder.IndexOf(range.End);
        if (startIndex < 0 || endIndex <= startIndex)
        {
            throw Error(
                "FRONT_MATTER_VERSION_TABLE_BOOKMARK_RANGE_INVALID",
                $"The {bookmarkName} bookmark range is invalid.");
        }

        var tables = new HashSet<Table>();
        foreach (var element in documentOrder.Skip(startIndex + 1).Take(endIndex - startIndex - 1))
        {
            if (element is Table table)
            {
                tables.Add(table);
            }
            var ancestor = element.Ancestors<Table>().FirstOrDefault();
            if (ancestor is not null)
            {
                tables.Add(ancestor);
            }
        }
        foreach (var ancestor in range.Start.Ancestors<Table>().Concat(range.End.Ancestors<Table>()))
        {
            tables.Add(ancestor);
        }

        if (tables.Count != 1)
        {
            throw Error(
                "FRONT_MATTER_VERSION_TABLE_BOOKMARK_RANGE_INVALID",
                $"The {bookmarkName} bookmark range must contain exactly one table.");
        }
        return tables.Single();
    }

    private static void RebindBookmarkToTable(BookmarkRange range, Table table, string bookmarkName)
    {
        if (table.Parent is not OpenXmlCompositeElement parent)
        {
            throw Error(
                "FRONT_MATTER_VERSION_TABLE_BOOKMARK_RANGE_INVALID",
                $"The {bookmarkName} table cannot be rebound to its bookmark.");
        }

        var id = range.Start.Id!.Value!;
        range.Start.Remove();
        range.End.Remove();
        parent.InsertBefore(new BookmarkStart { Name = bookmarkName, Id = id }, table);
        parent.InsertAfter(new BookmarkEnd { Id = id }, table);
    }

    private static WorkerCommandException Error(string code, string message) =>
        new(code, message, 4, "openxml-finalize");

    private sealed record BookmarkRange(BookmarkStart Start, BookmarkEnd End);
}
