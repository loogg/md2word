using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Md2Word.Worker.Services;

internal sealed record TableHeaderFinalizationResult(
    int TablesVisited,
    int HeaderRowsEnabled,
    int HeaderFlagsRemoved);

internal static class OpenXmlTableHeaderFinalizer
{
    public static TableHeaderFinalizationResult Finalize(
        string docxPath,
        bool enabled,
        bool bodyOnly)
    {
        using var document = WordprocessingDocument.Open(docxPath, true);
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidDataException("The generated DOCX has no main document part.");
        var wordDocument = mainPart.Document
            ?? throw new InvalidDataException("The generated DOCX has no main document.");
        var body = wordDocument.Body
            ?? throw new InvalidDataException("The generated DOCX has no document body.");

        var tables = EnumerateTargetTables(body, bodyOnly).ToArray();
        var headerRowsEnabled = 0;
        var headerFlagsRemoved = 0;
        foreach (var table in tables)
        {
            if (enabled)
            {
                var firstRow = table.Elements<TableRow>().FirstOrDefault();
                if (firstRow is null)
                {
                    continue;
                }

                foreach (var row in table.Elements<TableRow>())
                {
                    var existingProperties = row.GetFirstChild<TableRowProperties>();
                    if (existingProperties is null)
                    {
                        continue;
                    }
                    foreach (var header in existingProperties.Elements<TableHeader>().ToArray())
                    {
                        header.Remove();
                        headerFlagsRemoved++;
                    }
                    if (!existingProperties.ChildElements.Any())
                    {
                        existingProperties.Remove();
                    }
                }

                var properties = firstRow.GetFirstChild<TableRowProperties>();
                if (properties is null)
                {
                    properties = new TableRowProperties();
                    firstRow.PrependChild(properties);
                }
                properties.Append(new TableHeader());
                headerRowsEnabled++;
                continue;
            }

            foreach (var row in table.Elements<TableRow>())
            {
                var properties = row.GetFirstChild<TableRowProperties>();
                if (properties is null)
                {
                    continue;
                }
                foreach (var header in properties.Elements<TableHeader>().ToArray())
                {
                    header.Remove();
                    headerFlagsRemoved++;
                }
                if (!properties.ChildElements.Any())
                {
                    properties.Remove();
                }
            }
        }

        wordDocument.Save();
        return new TableHeaderFinalizationResult(tables.Length, headerRowsEnabled, headerFlagsRemoved);
    }

    private static IEnumerable<Table> EnumerateTargetTables(Body body, bool bodyOnly)
    {
        if (!bodyOnly)
        {
            return body.Descendants<Table>().ToArray();
        }

        var result = new List<Table>();
        var inside = false;
        foreach (var child in body.ChildElements)
        {
            if (!inside && ContainsBookmark(child, TemplateValidationService.BodyStartBookmark))
            {
                inside = true;
            }
            if (inside)
            {
                if (child is Table table)
                {
                    result.Add(table);
                }
                else
                {
                    result.AddRange(child.Descendants<Table>());
                }
            }
            if (inside && ContainsBookmark(child, TemplateValidationService.BodyEndBookmark))
            {
                inside = false;
            }
        }
        return result;
    }

    private static bool ContainsBookmark(OpenXmlElement element, string bookmarkName) =>
        (element as BookmarkStart)?.Name?.Value == bookmarkName
        || element.Descendants<BookmarkStart>().Any(bookmark => bookmark.Name?.Value == bookmarkName);
}
