using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Md2Word.Worker.Infrastructure;
using Md2Word.Worker.Protocol;
using Word = Microsoft.Office.Interop.Word;

namespace Md2Word.Worker.Services;

internal sealed class WordAutomationService
{
    internal const float ImageWidthTolerancePoints = 1f;
    private static readonly Regex HeadingStyleRoleMarkerRegex = new(
        @"__MD2WORD_ROLE_[0-9a-fA-F]{32}_h(?<level>[1-6])__",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NumberedHeadingRoleMarkerRegex = new(
        @"__MD2WORD_ROLE_[0-9a-fA-F]{32}_h(?<level>[1-4])__",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void BuildTemplateDocument(
        string htmlPath,
        string templatePath,
        string outputPath,
        PandocMetadata metadata,
        IReadOnlyDictionary<string, ResolvedStyle> resolvedRoleStyles,
        CancellationToken cancellationToken)
    {
        var intermediatePath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(outputPath)}.content.docx");
        var tracker = new ComObjectTracker();
        Word.Application? application = null;
        Word.Document? probeDocument = null;
        Word.Document? htmlDocument = null;
        Word.Document? sourceDocument = null;
        Word.Document? targetDocument = null;
        Word.Options? wordOptions = null;
        bool? previousUpdateLinksAtOpen = null;
        int? ownedWordProcessId = null;
        CancellationTokenRegistration cancellationRegistration = default;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            application = tracker.Track(new Word.Application
            {
                Visible = false,
                DisplayAlerts = Word.WdAlertLevel.wdAlertsNone,
                ScreenUpdating = false,
            });
            wordOptions = tracker.Track(application.Options);
            previousUpdateLinksAtOpen = wordOptions.UpdateLinksAtOpen;
            wordOptions.UpdateLinksAtOpen = false;
            var documents = tracker.Track(application.Documents);
            probeDocument = tracker.Track(documents.Add(Visible: false));
            var probeWindow = tracker.Track(probeDocument.ActiveWindow);
            ownedWordProcessId = TryGetProcessId(probeWindow.Hwnd);
            if (ownedWordProcessId is { } processId)
            {
                cancellationRegistration = cancellationToken.Register(() => KillOwnedWordProcess(processId));
            }
            probeDocument.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
            probeDocument = null;

            htmlDocument = tracker.Track(documents.Open(
                FileName: htmlPath,
                ConfirmConversions: false,
                ReadOnly: false,
                AddToRecentFiles: false,
                Visible: false,
                OpenAndRepair: false));
            htmlDocument.SaveAs2(
                FileName: intermediatePath,
                FileFormat: Word.WdSaveFormat.wdFormatXMLDocument,
                AddToRecentFiles: false);
            htmlDocument.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
            htmlDocument = null;

            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(templatePath, outputPath, overwrite: true);
            sourceDocument = tracker.Track(documents.Open(
                FileName: intermediatePath,
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false,
                Visible: false,
                OpenAndRepair: false));
            targetDocument = tracker.Track(documents.Open(
                FileName: outputPath,
                ConfirmConversions: false,
                ReadOnly: false,
                AddToRecentFiles: false,
                Visible: false,
                OpenAndRepair: false));

            var insertedBody = CopyIntoBodyRange(sourceDocument, targetDocument, tracker);
            var insertedBodyRange = tracker.Track(targetDocument.Range(
                Start: insertedBody.Start,
                End: insertedBody.End));
            ApplyHeadingTemplateStyles(insertedBodyRange, resolvedRoleStyles, tracker);
            ApplyHeadingNumbering(targetDocument, insertedBodyRange, metadata, tracker);
            cancellationToken.ThrowIfCancellationRequested();
            targetDocument.SaveAs2(
                FileName: outputPath,
                FileFormat: Word.WdSaveFormat.wdFormatXMLDocument,
                AddToRecentFiles: false);
            targetDocument.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
            targetDocument = null;
            sourceDocument.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
            sourceDocument = null;

            // Word imports local HTML images as linked InlineShapes. Their Width
            // and Height setters can be silently ignored while the external link
            // is still active. Embed those readable local targets in the closed
            // package before reopening it for deterministic layout finalization.
            var offlineSanitizer = new GeneratedDocumentOfflineSanitizer();
            var localImageSanitization = offlineSanitizer.EmbedLinkedLocalImages(outputPath);

            if (metadata.HeadingNumbering)
            {
                OpenXmlHeadingNumberingNormalizer.Normalize(outputPath);
            }

            // Word can recalculate imported picture dimensions only after the
            // assembled DOCX has been persisted. Reopen the final package, then
            // constrain body images and refresh fields against the final layout.
            cancellationToken.ThrowIfCancellationRequested();
            targetDocument = tracker.Track(documents.Open(
                FileName: outputPath,
                ConfirmConversions: false,
                ReadOnly: false,
                AddToRecentFiles: false,
                Visible: false,
                OpenAndRepair: false));
            var reopenedBodyRange = GetBodyRange(targetDocument, tracker);
            ReinsertRecoveredLocalImages(
                targetDocument,
                localImageSanitization.BodyImageRecoveries,
                tracker);
            NormalizeInlineImageSizes(targetDocument, reopenedBodyRange, tracker);
            cancellationToken.ThrowIfCancellationRequested();
            UpdateFields(targetDocument, tracker);
            targetDocument.Save();
            targetDocument.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
            targetDocument = null;
            offlineSanitizer.RestoreRecoveredImageMetadata(
                outputPath,
                localImageSanitization.BodyImageRecoveries);
            if (metadata.HeadingNumbering)
            {
                OpenXmlHeadingNumberingNormalizer.Normalize(outputPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkerCommandException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Word automation was canceled.", exception, cancellationToken);
            }
            throw new WorkerCommandException("WORD_AUTOMATION_FAILED", "Microsoft Word could not assemble the document.", 4, "word-import", retryable: true, inner: exception);
        }
        finally
        {
            cancellationRegistration.Dispose();
            CloseWithoutSaving(targetDocument);
            CloseWithoutSaving(sourceDocument);
            CloseWithoutSaving(htmlDocument);
            CloseWithoutSaving(probeDocument);
            if (wordOptions is not null && previousUpdateLinksAtOpen is { } previousValue)
            {
                try
                {
                    wordOptions.UpdateLinksAtOpen = previousValue;
                }
                catch
                {
                    // The dedicated Word instance may already have terminated.
                }
            }
            if (application is not null)
            {
                try
                {
                    application.Quit(Word.WdSaveOptions.wdDoNotSaveChanges);
                }
                catch
                {
                    // The dedicated Word instance may already have terminated.
                }
            }
            tracker.Dispose();
            // Explicit COM release above is the primary cleanup mechanism. These
            // collections only drain compiler-created transient RCWs so the
            // dedicated WINWORD process can terminate before the Worker exits.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            EnsureOwnedWordProcessStopped(ownedWordProcessId);
            AtomicFile.TryDelete(intermediatePath);
        }
    }

    private static (int Start, int End) CopyIntoBodyRange(
        Word.Document source,
        Word.Document target,
        ComObjectTracker tracker)
    {
        var bookmarks = tracker.Track(target.Bookmarks);
        object startName = TemplateValidationService.BodyStartBookmark;
        object endName = TemplateValidationService.BodyEndBookmark;
        if (!bookmarks.Exists((string)startName) || !bookmarks.Exists((string)endName))
        {
            throw new WorkerCommandException("BODY_BOOKMARK_MISSING", "The template body bookmarks disappeared before assembly.", 4, "template-assembly");
        }

        var startBookmark = tracker.Track(bookmarks.get_Item(ref startName));
        var endBookmark = tracker.Track(bookmarks.get_Item(ref endName));
        var startBookmarkRange = tracker.Track(startBookmark.Range);
        var endBookmarkRange = tracker.Track(endBookmark.Range);
        if (startBookmarkRange.Start >= endBookmarkRange.Start)
        {
            throw new WorkerCommandException("BODY_BOOKMARK_ORDER_INVALID", "The template body bookmark order is invalid.", 4, "template-assembly");
        }

        var targetRange = tracker.Track(target.Range(Start: startBookmarkRange.End, End: endBookmarkRange.Start));
        var sourceContent = tracker.Track(source.Content);
        var sourceRange = tracker.Track(sourceContent.Duplicate);
        if (sourceRange.End > sourceRange.Start)
        {
            sourceRange.MoveEnd(Word.WdUnits.wdCharacter, -1);
        }
        var formattedText = tracker.Track(sourceRange.FormattedText);
        startBookmark.Delete();
        endBookmark.Delete();
        targetRange.FormattedText = formattedText;
        var insertedStart = targetRange.Start;
        var insertedEnd = targetRange.End;

        var startMarker = tracker.Track(target.Range(Start: insertedStart, End: insertedStart));
        var endMarker = tracker.Track(target.Range(Start: insertedEnd, End: insertedEnd));
        tracker.Track(bookmarks.Add(TemplateValidationService.BodyStartBookmark, startMarker));
        tracker.Track(bookmarks.Add(TemplateValidationService.BodyEndBookmark, endMarker));
        return (insertedStart, insertedEnd);
    }

    private static Word.Range GetBodyRange(Word.Document document, ComObjectTracker tracker)
    {
        var bookmarks = tracker.Track(document.Bookmarks);
        object startName = TemplateValidationService.BodyStartBookmark;
        object endName = TemplateValidationService.BodyEndBookmark;
        if (!bookmarks.Exists((string)startName) || !bookmarks.Exists((string)endName))
        {
            throw new WorkerCommandException(
                "BODY_BOOKMARK_MISSING",
                "The assembled document body bookmarks disappeared before layout finalization.",
                4,
                "template-assembly");
        }

        var startBookmark = tracker.Track(bookmarks.get_Item(ref startName));
        var endBookmark = tracker.Track(bookmarks.get_Item(ref endName));
        var startRange = tracker.Track(startBookmark.Range);
        var endRange = tracker.Track(endBookmark.Range);
        if (startRange.Start >= endRange.Start)
        {
            throw new WorkerCommandException(
                "BODY_BOOKMARK_ORDER_INVALID",
                "The assembled document body bookmark order is invalid.",
                4,
                "template-assembly");
        }
        return tracker.Track(document.Range(Start: startRange.Start, End: endRange.Start));
    }

    private static void ApplyHeadingTemplateStyles(
        Word.Range bodyRange,
        IReadOnlyDictionary<string, ResolvedStyle> resolvedRoleStyles,
        ComObjectTracker tracker)
    {
        var paragraphs = tracker.Track(bodyRange.Paragraphs);
        for (var index = 1; index <= paragraphs.Count; index++)
        {
            var paragraph = tracker.Track(paragraphs[index]);
            var paragraphRange = tracker.Track(paragraph.Range);
            var match = HeadingStyleRoleMarkerRegex.Match(paragraphRange.Text ?? string.Empty);
            if (!match.Success
                || !int.TryParse(match.Groups["level"].Value, out var level)
                || !resolvedRoleStyles.TryGetValue($"h{level}", out var resolvedStyle))
            {
                continue;
            }

            ApplyParagraphStyle(paragraphRange, resolvedStyle);
            var paragraphFormat = tracker.Track(paragraphRange.ParagraphFormat);
            ((dynamic)paragraphFormat).Reset();
            var font = tracker.Track(paragraphRange.Font);
            ((dynamic)font).Reset();
            ApplyParagraphStyle(paragraphRange, resolvedStyle);
        }
    }

    private static void ApplyParagraphStyle(Word.Range paragraphRange, ResolvedStyle resolvedStyle)
    {
        object style = resolvedStyle.WordBuiltInStyleId is { } builtInStyleId
            ? (Word.WdBuiltinStyle)builtInStyleId
            : resolvedStyle.Name;
        paragraphRange.set_Style(ref style);
    }

    private static void NormalizeInlineImageSizes(
        Word.Document document,
        Word.Range bodyRange,
        ComObjectTracker tracker)
    {
        var bodyStart = bodyRange.Start;
        var bodyEnd = bodyRange.End;
        var inlineShapes = tracker.Track(document.InlineShapes);
        for (var index = 1; index <= inlineShapes.Count; index++)
        {
            var shape = tracker.Track(inlineShapes[index]);
            var shapeRange = tracker.Track(shape.Range);
            if (shapeRange.StoryType != Word.WdStoryType.wdMainTextStory
                || shapeRange.Start < bodyStart
                || shapeRange.Start >= bodyEnd)
            {
                continue;
            }
            var paragraphs = tracker.Track(shapeRange.Paragraphs);
            if (paragraphs.Count == 0)
            {
                continue;
            }
            var paragraph = tracker.Track(paragraphs[1]);
            var paragraphRange = tracker.Track(paragraph.Range);
            var paragraphFormat = tracker.Track(paragraphRange.ParagraphFormat);
            // A template style may use exact line spacing for body text. Word
            // clips an inline picture to that fixed line box even when the
            // DrawingML extent is correct, so image-bearing body paragraphs
            // need an auto-growing line box.
            paragraphFormat.LineSpacingRule = Word.WdLineSpacing.wdLineSpaceSingle;
            var sections = tracker.Track(paragraphRange.Sections);
            if (sections.Count == 0)
            {
                continue;
            }
            var section = tracker.Track(sections[1]);
            var pageSetup = tracker.Track(section.PageSetup);
            var availableWidth = CalculateAvailableImageWidth(
                pageSetup.PageWidth,
                pageSetup.LeftMargin,
                pageSetup.RightMargin,
                paragraphFormat.LeftIndent,
                paragraphFormat.RightIndent);
            var originalWidth = shape.Width;
            var originalHeight = shape.Height;
            var constrained = ConstrainImageSize(originalWidth, originalHeight, availableWidth);
            if (!constrained.Resized)
            {
                continue;
            }
            // Word can ignore direct Width/Height assignments on pictures that
            // originated as linked HTML fields, even after the package relation
            // has been embedded. Its native scaling operation updates the field
            // result reliably and preserves the picture's locked aspect ratio.
            var scale = constrained.Width / originalWidth;
            var originalScaleWidth = shape.ScaleWidth;
            var originalScaleHeight = shape.ScaleHeight;
            shape.ScaleHeight = originalScaleHeight * scale;
            shape.ScaleWidth = originalScaleWidth * scale;
            var finalWidth = shape.Width;
            if (finalWidth > availableWidth + ImageWidthTolerancePoints)
            {
                throw new WorkerCommandException(
                    "IMAGE_SIZE_FINALIZATION_FAILED",
                    "A body image could not be constrained to the available Word layout width.",
                    4,
                    "word-import");
            }
        }
    }

    private static void ReinsertRecoveredLocalImages(
        Word.Document document,
        IReadOnlyList<EmbeddedLocalImageRecovery> recoveries,
        ComObjectTracker tracker)
    {
        foreach (var recovery in recoveries)
        {
            var bookmarks = tracker.Track(document.Bookmarks);
            if (!bookmarks.Exists(recovery.Marker))
            {
                throw ImageRecoveryFailed();
            }

            object bookmarkIndex = recovery.Marker;
            var bookmark = tracker.Track(bookmarks.get_Item(ref bookmarkIndex));
            var markedRange = tracker.Track(bookmark.Range);
            var markedShapes = tracker.Track(markedRange.InlineShapes);
            if (markedShapes.Count != 1)
            {
                throw ImageRecoveryFailed();
            }

            var shape = tracker.Track(markedShapes[1]);
            var shapeRange = tracker.Track(shape.Range);
            var insertionStart = shapeRange.Start;
            bookmark.Delete();
            shape.Delete();

            var insertionRange = tracker.Track(document.Range(
                Start: insertionStart,
                End: insertionStart));
            var insertionShapes = tracker.Track(insertionRange.InlineShapes);
            var replacement = tracker.Track(insertionShapes.AddPicture(
                FileName: recovery.LocalPath,
                LinkToFile: false,
                SaveWithDocument: true,
                Range: insertionRange));
            var replacementRange = tracker.Track(replacement.Range);
            tracker.Track(bookmarks.Add(recovery.Marker, replacementRange));
        }
    }

    private static WorkerCommandException ImageRecoveryFailed() => new(
        "IMAGE_SIZE_FINALIZATION_FAILED",
        "An embedded local image could not be restored from Word's linked-image placeholder.",
        4,
        "word-import");

    internal static float CalculateAvailableImageWidth(
        float pageWidth,
        float leftMargin,
        float rightMargin,
        float leftIndent,
        float rightIndent) =>
        pageWidth
        - leftMargin
        - rightMargin
        - Math.Max(leftIndent, 0)
        - Math.Max(rightIndent, 0);

    internal static (float Width, float Height, bool Resized) ConstrainImageSize(
        float width,
        float height,
        float availableWidth)
    {
        if (!float.IsFinite(width)
            || !float.IsFinite(height)
            || !float.IsFinite(availableWidth)
            || width <= 0
            || height <= 0
            || availableWidth <= 0
            || width <= availableWidth + ImageWidthTolerancePoints)
        {
            return (width, height, false);
        }
        var scale = availableWidth / width;
        return (availableWidth, height * scale, true);
    }

    private static void ApplyHeadingNumbering(
        Word.Document document,
        Word.Range bodyRange,
        PandocMetadata metadata,
        ComObjectTracker tracker)
    {
        if (!metadata.HeadingNumbering)
        {
            return;
        }

        var paragraphs = tracker.Track(bodyRange.Paragraphs);
        var headings = new List<(Word.Range Range, int Level)>();
        for (var index = 1; index <= paragraphs.Count; index++)
        {
            var paragraph = tracker.Track(paragraphs[index]);
            var paragraphRange = tracker.Track(paragraph.Range);
            var match = NumberedHeadingRoleMarkerRegex.Match(paragraphRange.Text ?? string.Empty);
            if (!match.Success || !int.TryParse(match.Groups["level"].Value, out var level))
            {
                continue;
            }
            headings.Add((paragraphRange, level));
        }
        var numberedHeadingIndexes = SelectNumberedHeadingIndexes(
            headings.Select(heading => heading.Level).ToArray(),
            metadata.HeadingNumberingStartBase);
        if (numberedHeadingIndexes.Count == 0)
        {
            return;
        }

        var listTemplates = tracker.Track(document.ListTemplates);
        var listTemplate = tracker.Track(listTemplates.Add(
            OutlineNumbered: true,
            Name: "MD2WordHeadingNumbering"));
        var listLevels = tracker.Track(listTemplate.ListLevels);
        for (var level = 1; level <= 4; level++)
        {
            var configured = metadata.EffectiveWordHeadingNumbering[level];
            var listLevel = tracker.Track(listLevels[level]);
            listLevel.NumberStyle = ResolveHeadingNumberStyle(configured.NumberStyle);
            listLevel.NumberFormat = configured.Format;
            listLevel.TrailingCharacter = Word.WdTrailingCharacter.wdTrailingSpace;
            listLevel.NumberPosition = 0;
            var textPosition = HeadingTextPositionPoints(level);
            listLevel.TextPosition = textPosition;
            listLevel.TabPosition = textPosition;
            listLevel.ResetOnHigher = level > 1 ? level - 1 : 0;
            listLevel.StartAt = 1;
        }

        foreach (var index in numberedHeadingIndexes)
        {
            var (paragraphRange, level) = headings[index];
            var listFormat = tracker.Track(paragraphRange.ListFormat);
            listFormat.ApplyListTemplateWithLevel(
                ListTemplate: listTemplate,
                ContinuePreviousList: true,
                ApplyTo: Word.WdListApplyTo.wdListApplyToSelection,
                DefaultListBehavior: Word.WdDefaultListBehavior.wdWord10ListBehavior,
                ApplyLevel: level);
        }
    }

    internal static IReadOnlyList<int> SelectNumberedHeadingIndexes(
        IReadOnlyList<int> levels,
        int startBase)
    {
        if (startBase <= 1)
        {
            return Enumerable.Range(0, levels.Count).ToArray();
        }

        var selected = new List<int>();
        var baseHeadingCount = 0;
        for (var index = 0; index < levels.Count; index++)
        {
            if (levels[index] == 1)
            {
                baseHeadingCount++;
            }
            if (baseHeadingCount >= startBase)
            {
                selected.Add(index);
            }
        }
        return selected;
    }

    private static Word.WdListNumberStyle ResolveHeadingNumberStyle(string numberStyle) => numberStyle switch
    {
        "decimal" => Word.WdListNumberStyle.wdListNumberStyleArabic,
        "upper_roman" => Word.WdListNumberStyle.wdListNumberStyleUppercaseRoman,
        "lower_roman" => Word.WdListNumberStyle.wdListNumberStyleLowercaseRoman,
        "upper_letter" => Word.WdListNumberStyle.wdListNumberStyleUppercaseLetter,
        "lower_letter" => Word.WdListNumberStyle.wdListNumberStyleLowercaseLetter,
        _ => throw new WorkerCommandException(
            "FRONT_MATTER_INVALID",
            $"Unsupported Word heading number style: {numberStyle}.",
            4,
            "metadata"),
    };

    private static float HeadingTextPositionPoints(int level) =>
        (0.74f + ((level - 1) * 0.45f)) * 28.35f;

    private static void UpdateFields(Word.Document document, ComObjectTracker tracker)
    {
        var fields = tracker.Track(document.Fields);
        var safeFieldTypes = new HashSet<Word.WdFieldType>
        {
            Word.WdFieldType.wdFieldPage,
            Word.WdFieldType.wdFieldNumPages,
            Word.WdFieldType.wdFieldRef,
            Word.WdFieldType.wdFieldPageRef,
            Word.WdFieldType.wdFieldSequence,
            Word.WdFieldType.wdFieldStyleRef,
            Word.WdFieldType.wdFieldDocProperty,
        };
        for (var index = 1; index <= fields.Count; index++)
        {
            var field = tracker.Track(fields[index]);
            if (safeFieldTypes.Contains(field.Type))
            {
                field.Update();
            }
        }
        var tablesOfContents = tracker.Track(document.TablesOfContents);
        for (var index = 1; index <= tablesOfContents.Count; index++)
        {
            var table = tracker.Track(tablesOfContents[index]);
            table.Update();
        }
    }

    private static void CloseWithoutSaving(Word.Document? document)
    {
        if (document is null)
        {
            return;
        }
        try
        {
            document.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
        }
        catch
        {
            // Preserve the primary conversion exception.
        }
    }

    private static int? TryGetProcessId(int windowHandle)
    {
        try
        {
            _ = GetWindowThreadProcessId((nint)windowHandle, out var processId);
            return processId > 0 ? checked((int)processId) : null;
        }
        catch (Exception exception) when (exception is COMException or OverflowException)
        {
            return null;
        }
    }

    private static void KillOwnedWordProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The dedicated process already exited.
        }
    }

    private static void EnsureOwnedWordProcessStopped(int? processId)
    {
        if (processId is null)
        {
            return;
        }
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.WaitForExit(10_000))
            {
                return;
            }

            // This PID was captured directly from the dedicated Word.Application created above;
            // never terminate an unverified user Word process.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (ArgumentException)
        {
            // The dedicated process already exited.
        }
        catch (InvalidOperationException)
        {
            // The process exited between checks.
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
