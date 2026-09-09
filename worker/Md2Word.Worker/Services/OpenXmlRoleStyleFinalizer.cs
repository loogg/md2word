using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;
using DrawingWordprocessing = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace Md2Word.Worker.Services;

internal sealed record RoleStyleFinalizationResult(int MarkersRemoved, int StylesApplied);

internal static partial class OpenXmlRoleStyleFinalizer
{
    private const string RoleMarkerStyleId = "md2word-role-marker";
    private const uint AdmonitionBorderSize = 18U;

    private sealed record AdmonitionPresentation(string FillColor, string BorderColor);

    private static readonly AdmonitionPresentation GenericAdmonitionPresentation = new("F8FAFC", "64748B");
    private static readonly AdmonitionPresentation NoteAdmonitionPresentation = new("EFF6FF", "2563EB");
    private static readonly AdmonitionPresentation CautionAdmonitionPresentation = new("FFFBEB", "B45309");
    private static readonly AdmonitionPresentation WarningAdmonitionPresentation = new("FEF2F2", "B91C1C");
    private static readonly AdmonitionPresentation DangerAdmonitionPresentation = new("FEE2E2", "7F1D1D");

    [GeneratedRegex(@"__MD2WORD_ROLE_[0-9a-fA-F]{32}_(?<role>[a-z0-9-]+)__", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();

    public static RoleStyleFinalizationResult Finalize(
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
        var markersRemoved = 0;
        var stylesApplied = 0;

        var targetParagraphs = EnumerateTargetParagraphs(body, bodyOnly).ToArray();
        foreach (var paragraph in targetParagraphs)
        {
            var matches = MarkerRegex().Matches(paragraph.InnerText);
            if (matches.Count == 0)
            {
                continue;
            }

            var roles = matches
                .Select(match => match.Groups["role"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            markersRemoved += RemoveMarkerText(paragraph);

            if (roles.Contains(HtmlConversionService.ImportBoundaryRole, StringComparer.Ordinal))
            {
                // Word may place the rebuilt zero-length body-end bookmark in
                // the synthetic import-boundary paragraph. Keep that now-empty
                // paragraph as the bookmark host; deleting it would silently
                // widen every later body-only finalizer to the document end.
                if (!ContainsPairedBookmark(paragraph, TemplateValidationService.BodyEndBookmark))
                {
                    paragraph.Remove();
                }
                continue;
            }

            var role = SelectMostSpecificRole(roles);
            if (role is null || !resolvedRoleStyles.TryGetValue(role, out var resolvedStyle))
            {
                continue;
            }
            var properties = paragraph.GetFirstChild<ParagraphProperties>();
            if (properties is null)
            {
                properties = new ParagraphProperties();
                paragraph.PrependChild(properties);
            }
            var preservedJustification = role == StyleRoles.FigureImage
                ? properties.GetFirstChild<Justification>()?.CloneNode(true) as Justification
                : null;
            SetParagraphStyle(properties, resolvedStyle.StyleId);
            ClearDirectParagraphFormatting(properties);
            if (preservedJustification is not null)
            {
                properties.Justification = preservedJustification;
            }
            if (role == StyleRoles.FigureImage)
            {
                properties.SpacingBetweenLines = AutoImageLineSpacing();
            }
            if (role == StyleRoles.Admonition)
            {
                ApplyAdmonitionPresentation(properties, ResolveAdmonitionPresentation(roles));
            }
            if (role is StyleRoles.Heading1 or StyleRoles.Heading2 or StyleRoles.Heading3 or StyleRoles.Heading4
                or StyleRoles.Heading5 or StyleRoles.Heading6 or StyleRoles.CodeBlock)
            {
                ClearDirectRunFormatting(paragraph);
            }
            else
            {
                RemoveImporterOnlyRunFontHints(paragraph);
            }
            stylesApplied++;
        }

        // List items and other imported paragraphs can contain inline pictures
        // without carrying the dedicated figure-image role. Role finalization
        // clears their direct paragraph geometry, so restore an auto-growing
        // line box for every inline DrawingML picture in the scoped body range.
        foreach (var paragraph in targetParagraphs
                     .Where(paragraph => paragraph.Descendants<DrawingWordprocessing.Inline>().Any()))
        {
            var properties = paragraph.GetFirstChild<ParagraphProperties>();
            if (properties is null)
            {
                properties = new ParagraphProperties();
                paragraph.PrependChild(properties);
            }
            properties.SpacingBetweenLines = AutoImageLineSpacing();
        }

        NormalizeAdmonitionLabelStyle(mainPart, targetParagraphs);

        // Word refreshes template TOCs before the Open XML role markers are
        // removed from imported headings. Those cached TOC result paragraphs
        // live outside the body bookmark range, so clean marker text there
        // without applying body styles to fixed template content.
        if (bodyOnly)
        {
            var targetSet = targetParagraphs.ToHashSet();
            foreach (var paragraph in body.Descendants<Paragraph>().Where(paragraph => !targetSet.Contains(paragraph)))
            {
                markersRemoved += RemoveMarkerText(paragraph);
            }
        }

        EnsureNoRoleMarkersRemain(body);
        RemoveUnusedTransientImportStyles(mainPart, wordDocument);

        wordDocument.Save();
        return new RoleStyleFinalizationResult(markersRemoved, stylesApplied);
    }

    private static SpacingBetweenLines AutoImageLineSpacing() => new()
    {
        Line = "240",
        LineRule = LineSpacingRuleValues.Auto,
    };

    private static string? SelectMostSpecificRole(IReadOnlyCollection<string> roles)
    {
        foreach (var role in new[]
        {
            StyleRoles.CodeBlock,
            StyleRoles.Caption,
            StyleRoles.TableCaption,
            StyleRoles.FigureImage,
            StyleRoles.Table,
            StyleRoles.Admonition,
            StyleRoles.OrderedList,
            StyleRoles.UnorderedList,
            StyleRoles.Heading1,
            StyleRoles.Heading2,
            StyleRoles.Heading3,
            StyleRoles.Heading4,
            StyleRoles.Heading5,
            StyleRoles.Heading6,
            StyleRoles.Body,
        })
        {
            if (roles.Contains(role, StringComparer.Ordinal))
            {
                return role;
            }
        }
        return null;
    }

    private static AdmonitionPresentation ResolveAdmonitionPresentation(IReadOnlyCollection<string> roles)
    {
        if (roles.Contains(HtmlConversionService.AdmonitionVisualDangerRole, StringComparer.Ordinal))
        {
            return DangerAdmonitionPresentation;
        }
        if (roles.Contains(HtmlConversionService.AdmonitionVisualWarningRole, StringComparer.Ordinal))
        {
            return WarningAdmonitionPresentation;
        }
        if (roles.Contains(HtmlConversionService.AdmonitionVisualCautionRole, StringComparer.Ordinal))
        {
            return CautionAdmonitionPresentation;
        }
        if (roles.Contains(HtmlConversionService.AdmonitionVisualNoteRole, StringComparer.Ordinal))
        {
            return NoteAdmonitionPresentation;
        }
        return GenericAdmonitionPresentation;
    }

    private static void ApplyAdmonitionPresentation(
        ParagraphProperties properties,
        AdmonitionPresentation presentation)
    {
        properties.AddChild(
            new ParagraphBorders(
                new LeftBorder
                {
                    Val = BorderValues.Single,
                    Color = presentation.BorderColor,
                    Size = AdmonitionBorderSize,
                    Space = 0U,
                }),
            true);
        properties.AddChild(
            new Shading
            {
                Val = ShadingPatternValues.Clear,
                Color = "auto",
                Fill = presentation.FillColor,
            },
            true);
    }

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

    private static bool ContainsPairedBookmark(OpenXmlElement element, string bookmarkName)
    {
        var starts = element.Descendants<BookmarkStart>()
            .Where(bookmark => bookmark.Name?.Value == bookmarkName)
            .Select(bookmark => bookmark.Id?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        return starts.Count > 0
            && element.Descendants<BookmarkEnd>()
                .Any(bookmark => starts.Contains(bookmark.Id?.Value));
    }

    private static void SetParagraphStyle(ParagraphProperties properties, string styleId)
    {
        var paragraphStyle = properties.GetFirstChild<ParagraphStyleId>();
        if (paragraphStyle is null)
        {
            paragraphStyle = new ParagraphStyleId();
            properties.PrependChild(paragraphStyle);
        }
        paragraphStyle.Val = styleId;
    }

    private static int RemoveMarkerText(Paragraph paragraph)
    {
        var textNodes = paragraph.Descendants<Text>().ToArray();
        if (textNodes.Length == 0)
        {
            return 0;
        }

        // Word can split the final underscores of a role marker into another
        // run and insert w:lastRenderedPageBreak between the runs when a table
        // crosses a page. Match against the concatenated visible text, then
        // project the removals back to the original w:t nodes so those split
        // markers cannot leak into the published document.
        var fullText = ConcatenateText(textNodes);
        var matches = MarkerRegex().Matches(fullText).Cast<Match>().ToArray();
        if (matches.Length == 0)
        {
            return 0;
        }

        var affectedRuns = new HashSet<Run>();
        var globalOffset = 0;
        foreach (var text in textNodes)
        {
            var current = text.Text ?? string.Empty;
            var textStart = globalOffset;
            var textEnd = textStart + current.Length;
            globalOffset = textEnd;
            var overlappingMatches = matches
                .Where(match => match.Index < textEnd && match.Index + match.Length > textStart)
                .ToArray();
            if (overlappingMatches.Length == 0)
            {
                continue;
            }

            var keptCharacters = current
                .Where((_, index) => !overlappingMatches.Any(match =>
                {
                    var absoluteIndex = textStart + index;
                    return absoluteIndex >= match.Index && absoluteIndex < match.Index + match.Length;
                }))
                .ToArray();
            var updated = new string(keptCharacters);
            var run = text.Ancestors<Run>().FirstOrDefault();
            if (run is not null)
            {
                affectedRuns.Add(run);
            }
            if (updated.Length == 0)
            {
                text.Remove();
            }
            else
            {
                text.Text = updated;
                text.Space = SpaceProcessingModeValues.Preserve;
            }
        }

        foreach (var run in affectedRuns)
        {
            var hasVisibleText = run.Descendants<Text>()
                .Any(text => !string.IsNullOrEmpty(text.Text));
            if (hasVisibleText)
            {
                foreach (var markerStyle in run.RunProperties?.Elements<RunStyle>()
                             .Where(style => string.Equals(
                                 style.Val?.Value,
                                 RoleMarkerStyleId,
                                 StringComparison.Ordinal))
                             .ToArray()
                         ?? [])
                {
                    markerStyle.Remove();
                }
                if (run.RunProperties is { HasChildren: false })
                {
                    run.RunProperties.Remove();
                }
                continue;
            }

            // Keep non-text pagination hints such as w:lastRenderedPageBreak,
            // but detach the now-empty marker character style. A run that has
            // no remaining semantic children can be removed entirely.
            run.RunProperties?.Remove();
            if (!run.ChildElements.Any())
            {
                run.Remove();
            }
        }
        return matches.Length;
    }

    private static string ConcatenateText(IEnumerable<Text> textNodes) =>
        string.Concat(textNodes.Select(text => text.Text ?? string.Empty));

    private static void EnsureNoRoleMarkersRemain(Body body)
    {
        var hasVisibleMarker = body.Descendants<Paragraph>()
            .Select(paragraph => ConcatenateText(paragraph.Descendants<Text>()))
            .Any(text => text.Contains("__MD2WORD_ROLE_", StringComparison.Ordinal));
        var hasMarkerStyleReference = body.Descendants<RunStyle>()
            .Any(style => string.Equals(
                style.Val?.Value,
                RoleMarkerStyleId,
                StringComparison.Ordinal));
        if (hasVisibleMarker || hasMarkerStyleReference)
        {
            throw new WorkerCommandException(
                "ROLE_MARKER_FINALIZATION_FAILED",
                "An internal Word role marker remained after Open XML finalization.",
                4,
                "openxml-finalize");
        }
    }

    private static void RemoveImporterOnlyRunFontHints(Paragraph paragraph)
    {
        foreach (var properties in paragraph.Descendants<RunProperties>().ToArray())
        {
            foreach (var fonts in properties.Elements<RunFonts>().ToArray())
            {
                if (fonts.GetAttributes().All(attribute => attribute.LocalName == "hint"))
                {
                    fonts.Remove();
                }
            }
            if (!properties.ChildElements.Any())
            {
                properties.Remove();
            }
        }
    }

    private static void NormalizeAdmonitionLabelStyle(
        MainDocumentPart mainPart,
        IEnumerable<Paragraph> targetParagraphs)
    {
        var strongStyleId = mainPart.StyleDefinitionsPart?.Styles?
            .Elements<Style>()
            .Where(style => style.Type?.Value == StyleValues.Character)
            .Where(style =>
                string.Equals(style.StyleId?.Value, "Strong", StringComparison.OrdinalIgnoreCase)
                || string.Equals(style.StyleName?.Val?.Value, "Strong", StringComparison.OrdinalIgnoreCase)
                || string.Equals(style.StyleName?.Val?.Value, "强调", StringComparison.OrdinalIgnoreCase))
            .Select(style => style.StyleId?.Value)
            .FirstOrDefault(styleId => !string.IsNullOrWhiteSpace(styleId));

        foreach (var run in targetParagraphs.SelectMany(paragraph => paragraph.Descendants<Run>()))
        {
            var properties = run.RunProperties;
            var runStyle = properties?.RunStyle;
            if (!string.Equals(runStyle?.Val?.Value, "admonition-label", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(strongStyleId))
            {
                runStyle!.Val = strongStyleId;
            }
            else
            {
                runStyle!.Remove();
                if (properties!.Bold is null)
                {
                    properties.Append(new Bold());
                }
            }
        }
    }

    private static void RemoveUnusedTransientImportStyles(
        MainDocumentPart mainPart,
        Document wordDocument)
    {
        var styles = mainPart.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            return;
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        AddStyleReferences(wordDocument, referenced);
        foreach (var header in mainPart.HeaderParts.Select(part => part.Header).Where(header => header is not null))
        {
            AddStyleReferences(header!, referenced);
        }
        foreach (var footer in mainPart.FooterParts.Select(part => part.Footer).Where(footer => footer is not null))
        {
            AddStyleReferences(footer!, referenced);
        }
        if (mainPart.FootnotesPart?.Footnotes is { } footnotes) AddStyleReferences(footnotes, referenced);
        if (mainPart.EndnotesPart?.Endnotes is { } endnotes) AddStyleReferences(endnotes, referenced);
        if (mainPart.WordprocessingCommentsPart?.Comments is { } comments) AddStyleReferences(comments, referenced);
        if (mainPart.NumberingDefinitionsPart?.Numbering is { } numbering)
        {
            foreach (var styleId in numbering.Descendants<ParagraphStyleIdInLevel>()
                .Select(style => style.Val?.Value)
                .Where(styleId => !string.IsNullOrWhiteSpace(styleId)))
            {
                referenced.Add(styleId!);
            }
        }

        foreach (var style in styles.Elements<Style>().Where(style => !IsTransientImportStyle(style)))
        {
            foreach (var styleId in new[]
            {
                style.BasedOn?.Val?.Value,
                style.NextParagraphStyle?.Val?.Value,
                style.LinkedStyle?.Val?.Value,
            }.Where(styleId => !string.IsNullOrWhiteSpace(styleId)))
            {
                referenced.Add(styleId!);
            }
        }

        foreach (var style in styles.Elements<Style>()
            .Where(IsTransientImportStyle)
            .Where(style => !referenced.Contains(style.StyleId?.Value ?? string.Empty))
            .ToArray())
        {
            style.Remove();
        }
        styles.Save();
    }

    private static void AddStyleReferences(OpenXmlElement root, ISet<string> referenced)
    {
        foreach (var styleId in root.Descendants<ParagraphStyleId>()
            .Select(style => style.Val?.Value)
            .Concat(root.Descendants<RunStyle>().Select(style => style.Val?.Value))
            .Where(styleId => !string.IsNullOrWhiteSpace(styleId)))
        {
            referenced.Add(styleId!);
        }
    }

    private static bool IsTransientImportStyle(Style style)
    {
        var styleId = style.StyleId?.Value ?? string.Empty;
        var styleName = style.StyleName?.Val?.Value ?? string.Empty;
        return styleId.StartsWith("manual-", StringComparison.OrdinalIgnoreCase)
            || styleName.StartsWith("manual-", StringComparison.OrdinalIgnoreCase)
            || styleId is RoleMarkerStyleId or "admonition-label"
            || styleName is RoleMarkerStyleId or "admonition-label";
    }

    private static void ClearDirectParagraphFormatting(ParagraphProperties properties)
    {
        foreach (var child in properties.ChildElements.ToArray())
        {
            if (child is ParagraphStyleId or NumberingProperties)
            {
                continue;
            }
            child.Remove();
        }
    }

    private static void ClearDirectRunFormatting(Paragraph paragraph)
    {
        foreach (var properties in paragraph.Descendants<RunProperties>().ToArray())
        {
            foreach (var child in properties.ChildElements.ToArray())
            {
                if (child is RunStyle)
                {
                    continue;
                }
                child.Remove();
            }
            if (!properties.ChildElements.Any())
            {
                properties.Remove();
            }
        }
    }
}
