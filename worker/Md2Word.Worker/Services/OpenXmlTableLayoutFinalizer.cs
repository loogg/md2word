using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Md2Word.Worker.Services;

internal sealed record TableLayoutFinalizationResult(
    int TablesVisited,
    int TablesSized,
    int MergedTablesSized,
    int TablesSkippedForIrregularGrid);

internal static class OpenXmlTableLayoutFinalizer
{
    private const int DefaultPageWidthTwips = 11_906;
    private const int DefaultMarginTwips = 1_134;

    public static TableLayoutFinalizationResult Finalize(string docxPath, bool bodyOnly)
    {
        using var document = WordprocessingDocument.Open(docxPath, true);
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidDataException("The generated DOCX has no main document part.");
        var wordDocument = mainPart.Document
            ?? throw new InvalidDataException("The generated DOCX has no main document.");
        var body = wordDocument.Body
            ?? throw new InvalidDataException("The generated DOCX has no document body.");

        var availableWidth = CalculateAvailableWidth(body);
        var tables = EnumerateTargetTables(body, bodyOnly).ToArray();
        var sized = 0;
        var mergedSized = 0;
        var skippedForIrregularGrid = 0;
        foreach (var table in tables)
        {
            var rows = table.Elements<TableRow>().ToArray();
            if (!TryResolveLogicalColumnCount(table, rows, out var columnCount))
            {
                skippedForIrregularGrid++;
                continue;
            }

            var hasMergedCells = rows
                .SelectMany(row => row.Elements<TableCell>())
                .Any(HasMergedCellMarkup);
            if (hasMergedCells)
            {
                // Word already translates regular HTML rowspan/colspan into
                // w:vMerge/w:gridSpan. Preserve those merge definitions and
                // AutoFit column proportions, but still finalize page width,
                // real borders, cell spacing, margins, vertical alignment and
                // compact cell paragraphs. Irregular grids remain out of scope.
                ApplyMergedTableWidth(table, availableWidth);
                ApplyTablePresentation(table);
                ApplyCellPresentation(rows, widths: null);
                mergedSized++;
            }
            else
            {
                var widths = ResolveColumnWidths(columnCount, availableWidth);
                ApplyTableWidth(table, availableWidth, widths);
                ApplyTablePresentation(table);
                ApplyCellPresentation(rows, widths);
            }
            sized++;
        }

        wordDocument.Save();
        return new TableLayoutFinalizationResult(
            tables.Length,
            sized,
            mergedSized,
            skippedForIrregularGrid);
    }

    private static int CalculateAvailableWidth(Body body)
    {
        var section = body.Elements<SectionProperties>().LastOrDefault()
            ?? body.Descendants<SectionProperties>().LastOrDefault();
        var pageSize = section?.GetFirstChild<PageSize>();
        var margins = section?.GetFirstChild<PageMargin>();
        var pageWidth = checked((int)(pageSize?.Width?.Value ?? DefaultPageWidthTwips));
        var left = checked((int)(margins?.Left?.Value ?? DefaultMarginTwips));
        var right = checked((int)(margins?.Right?.Value ?? DefaultMarginTwips));
        return Math.Max(1_440, pageWidth - left - right);
    }

    private static int[] ResolveColumnWidths(int columnCount, int availableWidth)
    {
        if (columnCount == 2)
        {
            // The audited Pandoc table filter emits the technical-report intent
            // as 24% / 76%. Word HTML import otherwise changes that to content-
            // based AutoFit, which can collapse a short Chinese key column.
            var first = (int)Math.Round(availableWidth * 0.24d, MidpointRounding.AwayFromZero);
            return [first, availableWidth - first];
        }

        var equal = availableWidth / columnCount;
        var widths = Enumerable.Repeat(equal, columnCount).ToArray();
        widths[^1] += availableWidth - widths.Sum();
        return widths;
    }

    private static void ApplyTableWidth(Table table, int availableWidth, IReadOnlyList<int> widths)
    {
        var properties = table.GetFirstChild<TableProperties>();
        if (properties is null)
        {
            properties = new TableProperties();
            table.PrependChild(properties);
        }

        properties.RemoveAllChildren<TableWidth>();
        properties.AddChild(new TableWidth
        {
            Type = TableWidthUnitValues.Dxa,
            Width = availableWidth.ToString(),
        }, true);
        properties.RemoveAllChildren<TableLayout>();
        properties.AddChild(new TableLayout { Type = TableLayoutValues.Fixed }, true);

        var existingGrid = table.GetFirstChild<TableGrid>();
        existingGrid?.Remove();
        var grid = new TableGrid(widths.Select(width => new GridColumn { Width = width.ToString() }));
        table.InsertAfter(grid, properties);
    }

    private static void ApplyMergedTableWidth(Table table, int availableWidth)
    {
        var properties = table.GetFirstChild<TableProperties>();
        if (properties is null)
        {
            properties = new TableProperties();
            table.PrependChild(properties);
        }

        properties.RemoveAllChildren<TableWidth>();
        properties.AddChild(new TableWidth
        {
            Type = TableWidthUnitValues.Dxa,
            Width = availableWidth.ToString(),
        }, true);
        properties.RemoveAllChildren<TableLayout>();
        properties.AddChild(new TableLayout { Type = TableLayoutValues.Autofit }, true);
    }

    private static void ApplyTablePresentation(Table table)
    {
        var properties = table.TableProperties!;
        // Word's HTML importer adds a small tblCellSpacing value. With real
        // grid borders that produces visible double rules between every cell,
        // unlike a native Word table. Remove it so adjacent borders collapse
        // into one continuous grid.
        properties.RemoveAllChildren<TableCellSpacing>();
        properties.RemoveAllChildren<TableIndentation>();
        properties.AddChild(new TableIndentation
        {
            Type = TableWidthUnitValues.Dxa,
            Width = 0,
        }, true);
        properties.RemoveAllChildren<TableJustification>();
        properties.AddChild(new TableJustification { Val = TableRowAlignmentValues.Left }, true);

        properties.RemoveAllChildren<TableBorders>();
        properties.AddChild(new TableBorders(
            Border<TopBorder>(),
            Border<LeftBorder>(),
            Border<BottomBorder>(),
            Border<RightBorder>(),
            Border<InsideHorizontalBorder>(),
            Border<InsideVerticalBorder>()), true);

        properties.RemoveAllChildren<TableCellMarginDefault>();
        properties.AddChild(new TableCellMarginDefault(
            new TopMargin { Type = TableWidthUnitValues.Dxa, Width = "80" },
            new TableCellLeftMargin { Type = TableWidthValues.Dxa, Width = 120 },
            new BottomMargin { Type = TableWidthUnitValues.Dxa, Width = "80" },
            new TableCellRightMargin { Type = TableWidthValues.Dxa, Width = 120 }), true);
    }

    private static T Border<T>() where T : BorderType, new() => new()
    {
        Val = BorderValues.Single,
        Color = "000000",
        Size = 4U,
        Space = 0U,
    };

    private static void ApplyCellPresentation(IReadOnlyList<TableRow> rows, IReadOnlyList<int>? widths)
    {
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var rowProperties = row.TableRowProperties;
            rowProperties?.RemoveAllChildren<TableCellSpacing>();
            if (rowProperties is not null && !rowProperties.ChildElements.Any())
            {
                rowProperties.Remove();
            }
            var cells = row.Elements<TableCell>().ToArray();
            for (var index = 0; index < cells.Length; index++)
            {
                var properties = cells[index].GetFirstChild<TableCellProperties>();
                if (properties is null)
                {
                    properties = new TableCellProperties();
                    cells[index].PrependChild(properties);
                }
                if (widths is not null)
                {
                    properties.RemoveAllChildren<TableCellWidth>();
                    properties.AddChild(new TableCellWidth
                    {
                        Type = TableWidthUnitValues.Dxa,
                        Width = widths[index].ToString(),
                    }, true);
                }
                properties.RemoveAllChildren<TableCellBorders>();
                properties.RemoveAllChildren<TableCellVerticalAlignment>();
                properties.AddChild(new TableCellVerticalAlignment
                {
                    Val = TableVerticalAlignmentValues.Center,
                }, true);

                foreach (var paragraph in cells[index].Descendants<Paragraph>())
                {
                    ApplyCellParagraphPresentation(paragraph, isHeader: rowIndex == 0);
                }
            }
        }
    }

    private static void ApplyCellParagraphPresentation(Paragraph paragraph, bool isHeader)
    {
        var properties = paragraph.ParagraphProperties;
        if (properties is null)
        {
            properties = new ParagraphProperties();
            paragraph.PrependChild(properties);
        }

        properties.RemoveAllChildren<SpacingBetweenLines>();
        properties.AddChild(new SpacingBetweenLines
        {
            Before = "0",
            After = "0",
            Line = "240",
            LineRule = LineSpacingRuleValues.Auto,
        }, true);

        if (properties.NumberingProperties is null)
        {
            properties.RemoveAllChildren<Indentation>();
            properties.AddChild(new Indentation
            {
                Left = "0",
                Right = "0",
                FirstLine = "0",
            }, true);
            properties.RemoveAllChildren<Justification>();
            properties.AddChild(new Justification
            {
                Val = isHeader ? JustificationValues.Center : JustificationValues.Left,
            }, true);
        }

        if (!isHeader)
        {
            return;
        }
        foreach (var run in paragraph.Descendants<Run>())
        {
            var runProperties = run.RunProperties;
            if (runProperties is null)
            {
                runProperties = new RunProperties();
                run.PrependChild(runProperties);
            }
            runProperties.RemoveAllChildren<Bold>();
            runProperties.AddChild(new Bold(), true);
            runProperties.RemoveAllChildren<BoldComplexScript>();
            runProperties.AddChild(new BoldComplexScript(), true);
        }
    }

    private static bool HasMergedCellMarkup(TableCell cell) =>
        cell.TableCellProperties?.GetFirstChild<GridSpan>()?.Val?.Value is > 1
        || cell.TableCellProperties?.GetFirstChild<VerticalMerge>() is not null;

    private static bool TryResolveLogicalColumnCount(
        Table table,
        IReadOnlyList<TableRow> rows,
        out int columnCount)
    {
        columnCount = 0;
        if (rows.Count == 0 || rows.Any(HasGridOffsets))
        {
            return false;
        }

        var existingGridColumns = table.TableGrid?.Elements<GridColumn>().Count() ?? 0;
        var rowWidths = new List<int>(rows.Count);
        foreach (var row in rows)
        {
            var logicalWidth = 0;
            foreach (var cell in row.Elements<TableCell>())
            {
                var span = cell.TableCellProperties?.GetFirstChild<GridSpan>()?.Val?.Value ?? 1;
                if (span <= 0)
                {
                    return false;
                }
                logicalWidth = checked(logicalWidth + span);
            }
            if (logicalWidth == 0)
            {
                return false;
            }
            rowWidths.Add(logicalWidth);
        }

        var resolvedColumnCount = existingGridColumns > 0 ? existingGridColumns : rowWidths[0];
        if (rowWidths.Any(width => width != resolvedColumnCount)
            || !HasValidVerticalMergeSequences(rows))
        {
            return false;
        }
        columnCount = resolvedColumnCount;
        return true;
    }

    private static bool HasValidVerticalMergeSequences(IReadOnlyList<TableRow> rows)
    {
        var activeMerges = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            var nextMerges = new Dictionary<int, int>();
            var logicalColumn = 0;
            foreach (var cell in row.Elements<TableCell>())
            {
                var span = cell.TableCellProperties?.GetFirstChild<GridSpan>()?.Val?.Value ?? 1;
                var merge = cell.TableCellProperties?.GetFirstChild<VerticalMerge>();
                if (merge is not null)
                {
                    if (merge.Val?.Value == MergedCellValues.Restart)
                    {
                        nextMerges[logicalColumn] = span;
                    }
                    else
                    {
                        if (!activeMerges.TryGetValue(logicalColumn, out var activeSpan)
                            || activeSpan != span)
                        {
                            return false;
                        }
                        nextMerges[logicalColumn] = span;
                    }
                }
                logicalColumn += span;
            }
            activeMerges = nextMerges;
        }
        return true;
    }

    private static bool HasGridOffsets(TableRow row) =>
        row.TableRowProperties?.GetFirstChild<GridBefore>()?.Val?.Value is > 0
        || row.TableRowProperties?.GetFirstChild<GridAfter>()?.Val?.Value is > 0;

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
