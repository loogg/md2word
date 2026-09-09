using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal sealed record InlineCodeFinalizationResult(
    int MarkersRemoved,
    int RangesStyled,
    int RunsStyled,
    int HtmlCodeStylesRemoved);

internal static class OpenXmlInlineCodeFinalizer
{
    private static readonly Regex MarkerRegex = new(
        @"__MD2WORD_INLINE_CODE_(?<kind>START|END)_(?<id>[0-9a-fA-F]{32})__",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static InlineCodeFinalizationResult Finalize(
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

        var paragraphs = EnumerateTargetParagraphs(body, bodyOnly).ToArray();
        var paragraphsWithMarkers = paragraphs
            .Where(paragraph => MarkerRegex.IsMatch(ConcatenateRunText(paragraph)))
            .ToArray();
        if (paragraphsWithMarkers.Length == 0)
        {
            return new InlineCodeFinalizationResult(0, 0, 0, 0);
        }
        if (!resolvedRoleStyles.TryGetValue(StyleRoles.InlineCode, out var targetStyle))
        {
            throw new WorkerCommandException(
                "INLINE_CODE_STYLE_MAPPING_MISSING",
                "Inline code is present but no Word paragraph style was resolved for it.",
                4,
                "openxml-finalize");
        }

        var effectiveTargetRunProperties = ResolveEffectiveRunProperties(mainPart, targetStyle.StyleId);
        var markersRemoved = 0;
        var rangesStyled = 0;
        var runsStyled = 0;
        foreach (var paragraph in paragraphsWithMarkers)
        {
            var result = FinalizeParagraph(paragraph, targetStyle.StyleId, effectiveTargetRunProperties);
            markersRemoved += result.MarkersRemoved;
            rangesStyled += result.RangesStyled;
            runsStyled += result.RunsStyled;
        }

        var htmlCodeStylesRemoved = RemoveUnusedHtmlCodeStyles(mainPart, wordDocument);
        wordDocument.Save();
        return new InlineCodeFinalizationResult(
            markersRemoved,
            rangesStyled,
            runsStyled,
            htmlCodeStylesRemoved);
    }

    private static InlineCodeFinalizationResult FinalizeParagraph(
        Paragraph paragraph,
        string targetStyleId,
        IReadOnlyList<OpenXmlElement> effectiveTargetRunProperties)
    {
        var sourceRuns = paragraph.Descendants<Run>()
            .Select(run => new SourceRun(run, string.Concat(run.Descendants<Text>().Select(text => text.Text ?? string.Empty))))
            .Where(item => item.Text.Length > 0)
            .ToArray();
        var fullText = string.Concat(sourceRuns.Select(item => item.Text));
        var matches = MarkerRegex.Matches(fullText).Cast<Match>().ToArray();
        if (matches.Length == 0)
        {
            return new InlineCodeFinalizationResult(0, 0, 0, 0);
        }

        var (segments, completedRangeCount) = BuildSegments(fullText.Length, matches);
        var paragraphStyleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var globalOffset = 0;
        var runsStyled = 0;
        foreach (var source in sourceRuns)
        {
            var runStart = globalOffset;
            var runEnd = runStart + source.Text.Length;
            globalOffset = runEnd;
            var overlaps = segments
                .Where(segment => segment.Start < runEnd && segment.End > runStart)
                .ToArray();
            if (!overlaps.Any(segment => segment.Kind is SegmentKind.Marker or SegmentKind.Inline))
            {
                continue;
            }

            foreach (var segment in overlaps)
            {
                var start = Math.Max(segment.Start, runStart);
                var end = Math.Min(segment.End, runEnd);
                if (end <= start || segment.Kind == SegmentKind.Marker)
                {
                    continue;
                }
                var text = source.Text.Substring(start - runStart, end - start);
                if (text.Length == 0)
                {
                    continue;
                }
                var replacement = CloneAsTextRun(source.Run, text);
                if (segment.Kind == SegmentKind.Inline)
                {
                    ApplyProjectedParagraphStyle(
                        replacement,
                        paragraphStyleId,
                        targetStyleId,
                        effectiveTargetRunProperties);
                    runsStyled++;
                }
                source.Run.InsertBeforeSelf(replacement);
            }
            source.Run.Remove();
        }

        return new InlineCodeFinalizationResult(matches.Length, completedRangeCount, runsStyled, 0);
    }

    private static (IReadOnlyList<TextSegment> Segments, int CompletedRangeCount) BuildSegments(
        int textLength,
        IReadOnlyList<Match> matches)
    {
        var segments = new List<TextSegment>();
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var startedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = 0;
        foreach (var match in matches)
        {
            if (match.Index > cursor)
            {
                segments.Add(new TextSegment(
                    cursor,
                    match.Index,
                    activeIds.Count > 0 ? SegmentKind.Inline : SegmentKind.Outside));
            }
            segments.Add(new TextSegment(match.Index, match.Index + match.Length, SegmentKind.Marker));

            var id = match.Groups["id"].Value;
            if (match.Groups["kind"].Value == "START")
            {
                activeIds.Add(id);
                startedIds.Add(id);
            }
            else
            {
                if (activeIds.Remove(id) && startedIds.Contains(id))
                {
                    completedIds.Add(id);
                }
            }
            cursor = match.Index + match.Length;
        }
        if (cursor < textLength)
        {
            segments.Add(new TextSegment(
                cursor,
                textLength,
                activeIds.Count > 0 ? SegmentKind.Inline : SegmentKind.Outside));
        }
        return (segments, completedIds.Count);
    }

    private static Run CloneAsTextRun(Run source, string text)
    {
        var clone = (Run)source.CloneNode(false);
        if (source.RunProperties is { } sourceProperties)
        {
            clone.Append(sourceProperties.CloneNode(true));
        }
        clone.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return clone;
    }

    private static void ApplyProjectedParagraphStyle(
        Run run,
        string? paragraphStyleId,
        string targetStyleId,
        IReadOnlyList<OpenXmlElement> effectiveTargetRunProperties)
    {
        var properties = run.RunProperties;
        if (properties is null)
        {
            properties = new RunProperties();
            run.PrependChild(properties);
        }
        properties.RemoveAllChildren();

        // When the paragraph already uses the requested style, an empty rPr is
        // the most faithful mapping: Word reports the selected inline range as
        // that paragraph style and inherits its complete computed formatting.
        // In lists, headings, or table cells with another paragraph style,
        // project the target style's effective run properties directly.
        if (!string.Equals(paragraphStyleId, targetStyleId, StringComparison.Ordinal))
        {
            foreach (var property in effectiveTargetRunProperties)
            {
                properties.Append(property.CloneNode(true));
            }
        }
        if (!properties.ChildElements.Any())
        {
            properties.Remove();
        }
    }

    private static IReadOnlyList<OpenXmlElement> ResolveEffectiveRunProperties(
        MainDocumentPart mainPart,
        string styleId)
    {
        var styles = mainPart.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            return [];
        }

        var chain = new Stack<Style>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentStyleId = styleId;
        while (!string.IsNullOrWhiteSpace(currentStyleId) && visited.Add(currentStyleId))
        {
            var style = styles.Elements<Style>()
                .FirstOrDefault(candidate => candidate.StyleId?.Value == currentStyleId);
            if (style is null)
            {
                break;
            }
            chain.Push(style);
            currentStyleId = style.BasedOn?.Val?.Value ?? string.Empty;
        }

        var merged = new List<OpenXmlElement>();
        foreach (var style in chain)
        {
            foreach (var property in style.StyleRunProperties?.ChildElements ?? [])
            {
                var existingIndex = merged.FindIndex(existing =>
                    existing.LocalName == property.LocalName
                    && existing.NamespaceUri == property.NamespaceUri);
                if (existingIndex >= 0)
                {
                    merged[existingIndex] = property.CloneNode(true);
                }
                else
                {
                    merged.Add(property.CloneNode(true));
                }
            }
        }
        return merged;
    }

    private static int RemoveUnusedHtmlCodeStyles(MainDocumentPart mainPart, Document wordDocument)
    {
        var styles = mainPart.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            return 0;
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        AddRunStyleReferences(wordDocument, referenced);
        foreach (var header in mainPart.HeaderParts.Select(part => part.Header).Where(header => header is not null))
        {
            AddRunStyleReferences(header!, referenced);
        }
        foreach (var footer in mainPart.FooterParts.Select(part => part.Footer).Where(footer => footer is not null))
        {
            AddRunStyleReferences(footer!, referenced);
        }
        if (mainPart.FootnotesPart?.Footnotes is { } footnotes) AddRunStyleReferences(footnotes, referenced);
        if (mainPart.EndnotesPart?.Endnotes is { } endnotes) AddRunStyleReferences(endnotes, referenced);
        if (mainPart.WordprocessingCommentsPart?.Comments is { } comments) AddRunStyleReferences(comments, referenced);

        var removed = 0;
        foreach (var style in styles.Elements<Style>()
            .Where(style => style.Type?.Value == StyleValues.Character)
            .Where(IsHtmlCodeStyle)
            .Where(style => !referenced.Contains(style.StyleId?.Value ?? string.Empty))
            .ToArray())
        {
            style.Remove();
            removed++;
        }
        if (removed > 0)
        {
            styles.Save();
        }
        return removed;
    }

    private static void AddRunStyleReferences(OpenXmlElement root, ISet<string> referenced)
    {
        foreach (var styleId in root.Descendants<RunStyle>()
            .Select(style => style.Val?.Value)
            .Where(styleId => !string.IsNullOrWhiteSpace(styleId)))
        {
            referenced.Add(styleId!);
        }
    }

    private static bool IsHtmlCodeStyle(Style style)
    {
        var id = NormalizeStyleKey(style.StyleId?.Value);
        var name = NormalizeStyleKey(style.StyleName?.Val?.Value);
        return id is "htmlcode" or "html代码"
            || name is "htmlcode" or "html代码";
    }

    private static string NormalizeStyleKey(string? value)
    {
        var builder = new StringBuilder();
        foreach (var character in value ?? string.Empty)
        {
            if (!char.IsWhiteSpace(character) && character is not '-' and not '_')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }
        return builder.ToString();
    }

    private static string ConcatenateRunText(Paragraph paragraph) =>
        string.Concat(paragraph.Descendants<Run>()
            .SelectMany(run => run.Descendants<Text>())
            .Select(text => text.Text ?? string.Empty));

    private static IEnumerable<Paragraph> EnumerateTargetParagraphs(Body body, bool bodyOnly)
    {
        if (!bodyOnly)
        {
            return body.Descendants<Paragraph>().ToArray();
        }

        var result = new List<Paragraph>();
        var inside = false;
        foreach (var child in body.ChildElements)
        {
            if (!inside && ContainsBookmark(child, TemplateValidationService.BodyStartBookmark))
            {
                inside = true;
            }
            if (inside)
            {
                if (child is Paragraph paragraph)
                {
                    result.Add(paragraph);
                }
                else
                {
                    result.AddRange(child.Descendants<Paragraph>());
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

    private sealed record SourceRun(Run Run, string Text);
    private sealed record TextSegment(int Start, int End, SegmentKind Kind);
    private enum SegmentKind
    {
        Outside,
        Inline,
        Marker,
    }
}
