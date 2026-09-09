using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal sealed record TableSeparatorFinalizationResult(
    int SeparatorsStyled,
    int HiddenSeparatorsRestored);

internal static class OpenXmlTableSeparatorFinalizer
{
    public static TableSeparatorFinalizationResult Finalize(
        string docxPath,
        IReadOnlyDictionary<string, ResolvedStyle> resolvedRoleStyles,
        bool bodyOnly)
    {
        using var document = WordprocessingDocument.Open(docxPath, true);
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidDataException("The generated DOCX has no main document part.");
        var wordDocument = mainPart.Document
            ?? throw new InvalidDataException("The generated DOCX has no main document.");
        var body = wordDocument.Body
            ?? throw new InvalidDataException("The generated DOCX has no document body.");

        var separators = EnumerateSafeTableSeparators(body, bodyOnly).ToArray();
        if (separators.Length == 0)
        {
            return new TableSeparatorFinalizationResult(0, 0);
        }
        if (!resolvedRoleStyles.TryGetValue(StyleRoles.Body, out var bodyStyle))
        {
            throw new WorkerCommandException(
                "TABLE_SEPARATOR_BODY_STYLE_MISSING",
                "Adjacent tables require the CSS-resolved body paragraph style for their empty separator paragraphs.",
                4,
                "openxml-finalize");
        }

        var hiddenSeparatorsRestored = 0;
        foreach (var paragraph in separators)
        {
            if (paragraph.Descendants<Vanish>().Any())
            {
                hiddenSeparatorsRestored++;
            }

            // Word's HTML importer inserts a hidden paragraph between adjacent
            // tables. Keep the required paragraph, but discard importer-only
            // direct formatting and let the CSS-mapped body style control its
            // spacing and line geometry. This references an existing template
            // style; it never creates a separator-specific Word style.
            paragraph.ParagraphProperties?.Remove();
            paragraph.PrependChild(new ParagraphProperties(
                new ParagraphStyleId { Val = bodyStyle.StyleId }));
            foreach (var run in paragraph.Elements<Run>().ToArray())
            {
                run.Remove();
            }
        }

        wordDocument.Save();
        return new TableSeparatorFinalizationResult(
            separators.Length,
            hiddenSeparatorsRestored);
    }

    private static IEnumerable<Paragraph> EnumerateSafeTableSeparators(Body body, bool bodyOnly)
    {
        var children = body.ChildElements.ToArray();
        var inScope = ResolveScope(children, bodyOnly);
        for (var index = 1; index < children.Length - 1; index++)
        {
            if (!inScope[index - 1] || !inScope[index] || !inScope[index + 1]
                || children[index - 1] is not Table
                || children[index] is not Paragraph paragraph
                || children[index + 1] is not Table
                || !IsStructurallyEmpty(paragraph))
            {
                continue;
            }
            yield return paragraph;
        }
    }

    private static bool[] ResolveScope(IReadOnlyList<OpenXmlElement> children, bool bodyOnly)
    {
        if (!bodyOnly)
        {
            return Enumerable.Repeat(true, children.Count).ToArray();
        }

        var result = new bool[children.Count];
        var inside = false;
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            if (!inside && ContainsBookmark(child, TemplateValidationService.BodyStartBookmark))
            {
                inside = true;
            }
            result[index] = inside;
            if (inside && ContainsBookmark(child, TemplateValidationService.BodyEndBookmark))
            {
                inside = false;
            }
        }
        return result;
    }

    private static bool IsStructurallyEmpty(Paragraph paragraph)
    {
        foreach (var child in paragraph.ChildElements)
        {
            if (child is ParagraphProperties)
            {
                continue;
            }
            if (child is not Run run)
            {
                return false;
            }
            if (run.ChildElements.Any(runChild => runChild is not RunProperties
                    && runChild is not Text { Text: "" }))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ContainsBookmark(OpenXmlElement element, string bookmarkName) =>
        (element as BookmarkStart)?.Name?.Value == bookmarkName
        || element.Descendants<BookmarkStart>().Any(bookmark => bookmark.Name?.Value == bookmarkName);
}
