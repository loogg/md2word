using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Word = Microsoft.Office.Interop.Word;

internal static class Program
{
    private const int WdDoNotSaveChanges = 0;
    private const int WdExportFormatPdf = 17;
    private const int WdExportOptimizeForPrint = 0;
    private const int WdExportAllDocument = 0;
    private const int WdExportDocumentContent = 0;
    private const int WdExportCreateHeadingBookmarks = 1;
    private const int WdStatisticPages = 2;
    private const int WdStatisticLines = 1;
    private const int WdActiveEndPageNumber = 3;
    private const int WdHorizontalPositionRelativeToPage = 5;
    private const int WdVerticalPositionRelativeToPage = 6;

    private static readonly string[] Sentinels =
    [
        "[FIXED-BEFORE]",
        "[CTRL-BODY-1]",
        "[CTRL-BODY-2]",
        "[CTRL-CODE]",
        "[DOM-BODY-1]",
        "[DOM-BODY-2]",
        "[CODE-L1]",
        "[LONG-01]",
        "[LONG-56]",
        "[DOM-END-BODY]",
        "[FIXED-AFTER]",
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            ExportAndMeasure(options.DocxPath, options.PdfPath, options.MetricsPath);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static Options ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Expected --docx, --pdf, and --metrics path pairs.");
            }

            values[args[index]] = args[index + 1];
        }

        var docxPath = RequirePath(values, "--docx", mustExist: true);
        var pdfPath = RequirePath(values, "--pdf", mustExist: false);
        var metricsPath = RequirePath(values, "--metrics", mustExist: false);

        if (!string.Equals(Path.GetExtension(docxPath), ".docx", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(metricsPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Expected .docx input, .pdf output, and .json metrics paths.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(pdfPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(metricsPath)!);
        return new Options(docxPath, pdfPath, metricsPath);
    }

    private static string RequirePath(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool mustExist)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required argument {key}.");
        }

        var fullPath = Path.GetFullPath(value);
        if (mustExist && !File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Input file does not exist: {fullPath}", fullPath);
        }

        return fullPath;
    }

    private static void ExportAndMeasure(string docxPath, string pdfPath, string metricsPath)
    {
        object? applicationObject = null;
        object? documentsObject = null;
        object? documentObject = null;
        object? paragraphsObject = null;
        var wordProcessId = 0;
        var stopwatch = Stopwatch.StartNew();
        var paragraphMetrics = new List<ParagraphMetric>();

        try
        {
            applicationObject = new Word.Application();
            dynamic application = applicationObject;
            application.Visible = false;
            application.DisplayAlerts = 0;

            var officeVersion = Convert.ToString(application.Version) ?? "unknown";
            var officeBuild = Convert.ToString(application.Build) ?? "unknown";

            documentsObject = application.Documents;
            dynamic documents = documentsObject;
            documentObject = documents.Open(
                FileName: docxPath,
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false,
                Visible: false,
                OpenAndRepair: false);
            dynamic document = documentObject;
            object? activeWindowObject = null;
            try
            {
                activeWindowObject = ((Word.Document)documentObject).ActiveWindow;
                var activeWindow = (Word.Window)activeWindowObject;
                _ = GetWindowThreadProcessId((nint)activeWindow.Hwnd, out var processId);
                wordProcessId = processId > 0 ? checked((int)processId) : 0;
            }
            finally
            {
                FinalRelease(activeWindowObject);
            }
            document.Repaginate();

            paragraphsObject = document.Paragraphs;
            dynamic paragraphs = paragraphsObject;
            var paragraphCount = Convert.ToInt32(paragraphs.Count);
            for (var index = 1; index <= paragraphCount; index++)
            {
                object? paragraphObject = null;
                object? rangeObject = null;
                object? formatObject = null;
                object? styleObject = null;
                try
                {
                    paragraphObject = paragraphs[index];
                    dynamic paragraph = paragraphObject;
                    rangeObject = paragraph.Range;
                    dynamic range = rangeObject;
                    var text = CleanText(Convert.ToString(range.Text) ?? string.Empty);
                    var sentinel = Sentinels.FirstOrDefault(
                        candidate => text.Contains(candidate, StringComparison.Ordinal));
                    if (sentinel is null)
                    {
                        continue;
                    }

                    formatObject = paragraph.Format;
                    dynamic format = formatObject;
                    styleObject = range.Style;
                    dynamic style = styleObject;

                    paragraphMetrics.Add(new ParagraphMetric(
                        Sentinel: sentinel,
                        Text: text,
                        StyleName: Convert.ToString(style.NameLocal) ?? Convert.ToString(style.Name) ?? "unknown",
                        Page: SafeInt(() => range.Information[WdActiveEndPageNumber]),
                        XPoints: SafeFloat(() => range.Information[WdHorizontalPositionRelativeToPage]),
                        YPoints: SafeFloat(() => range.Information[WdVerticalPositionRelativeToPage]),
                        LineCount: SafeInt(() => range.ComputeStatistics(WdStatisticLines)),
                        SpaceBeforePoints: Convert.ToSingle(format.SpaceBefore),
                        SpaceAfterPoints: Convert.ToSingle(format.SpaceAfter),
                        LineSpacingPoints: Convert.ToSingle(format.LineSpacing),
                        LineSpacingRule: Convert.ToInt32(format.LineSpacingRule),
                        LeftIndentPoints: Convert.ToSingle(format.LeftIndent),
                        RightIndentPoints: Convert.ToSingle(format.RightIndent),
                        FirstLineIndentPoints: Convert.ToSingle(format.FirstLineIndent),
                        KeepTogether: Convert.ToInt32(format.KeepTogether),
                        KeepWithNext: Convert.ToInt32(format.KeepWithNext)));
                }
                finally
                {
                    FinalRelease(styleObject);
                    FinalRelease(formatObject);
                    FinalRelease(rangeObject);
                    FinalRelease(paragraphObject);
                }
            }

            document.ExportAsFixedFormat(
                pdfPath,
                WdExportFormatPdf,
                false,
                WdExportOptimizeForPrint,
                WdExportAllDocument,
                1,
                1,
                WdExportDocumentContent,
                true,
                true,
                WdExportCreateHeadingBookmarks,
                true,
                true,
                false);

            var pageCount = Convert.ToInt32(document.ComputeStatistics(WdStatisticPages));
            var metric = new ExportMetric(
                SchemaVersion: 1,
                SourceDocx: docxPath,
                OutputPdf: pdfPath,
                OfficeVersion: officeVersion,
                OfficeBuild: officeBuild,
                Pages: pageCount,
                ParagraphsInspected: paragraphCount,
                ExportDurationMs: stopwatch.ElapsedMilliseconds,
                Paragraphs: paragraphMetrics);
            File.WriteAllText(
                metricsPath,
                JsonSerializer.Serialize(metric, new JsonSerializerOptions { WriteIndented = true }));

            FinalRelease(paragraphsObject);
            paragraphsObject = null;
            document.Close(WdDoNotSaveChanges);
            FinalRelease(documentObject);
            documentObject = null;
            FinalRelease(documentsObject);
            documentsObject = null;
            application.Quit(WdDoNotSaveChanges);
            FinalRelease(applicationObject);
            applicationObject = null;
        }
        finally
        {
            if (documentObject is not null)
            {
                try
                {
                    ((dynamic)documentObject).Close(WdDoNotSaveChanges);
                }
                catch
                {
                    // Preserve the original failure while still attempting process cleanup.
                }
            }

            if (applicationObject is not null)
            {
                try
                {
                    ((dynamic)applicationObject).Quit(WdDoNotSaveChanges);
                }
                catch
                {
                    // Preserve the original failure while still attempting process cleanup.
                }
            }

            FinalRelease(paragraphsObject);
            FinalRelease(documentObject);
            FinalRelease(documentsObject);
            FinalRelease(applicationObject);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            EnsureDedicatedWordProcessExited(wordProcessId);
        }

        if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
        {
            throw new InvalidOperationException("Word did not create a non-empty PDF.");
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            pdfPath,
            metricsPath,
            paragraphs = paragraphMetrics.Count,
        }));
    }

    private static string CleanText(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\a", string.Empty, StringComparison.Ordinal)
            .TrimEnd();

    private static int SafeInt(Func<dynamic> getter)
    {
        try
        {
            return Convert.ToInt32(getter());
        }
        catch
        {
            return -1;
        }
    }

    private static float SafeFloat(Func<dynamic> getter)
    {
        try
        {
            return Convert.ToSingle(getter());
        }
        catch
        {
            return -1;
        }
    }

    private static void FinalRelease(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static void EnsureDedicatedWordProcessExited(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(5_000))
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(5_000);
            }
        }
        catch (ArgumentException)
        {
            // The dedicated Word process already exited.
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    private sealed record Options(string DocxPath, string PdfPath, string MetricsPath);

    private sealed record ParagraphMetric(
        string Sentinel,
        string Text,
        string StyleName,
        int Page,
        float XPoints,
        float YPoints,
        int LineCount,
        float SpaceBeforePoints,
        float SpaceAfterPoints,
        float LineSpacingPoints,
        int LineSpacingRule,
        float LeftIndentPoints,
        float RightIndentPoints,
        float FirstLineIndentPoints,
        int KeepTogether,
        int KeepWithNext);

    private sealed record ExportMetric(
        int SchemaVersion,
        string SourceDocx,
        string OutputPdf,
        string OfficeVersion,
        string OfficeBuild,
        int Pages,
        int ParagraphsInspected,
        long ExportDurationMs,
        IReadOnlyList<ParagraphMetric> Paragraphs);
}
