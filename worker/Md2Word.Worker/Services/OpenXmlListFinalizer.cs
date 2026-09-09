using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;
using System.Text.RegularExpressions;

namespace Md2Word.Worker.Services;

internal sealed record ListFinalizationResult(int NormalizedParagraphs, int RestartedOrderedLists);

internal sealed class OpenXmlListFinalizer
{
    private static readonly Regex RoleMarkerRegex = new(
        @"__MD2WORD_ROLE_[0-9a-fA-F]{32}_(?<role>[a-z0-9-]+)__",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ListFinalizationResult Finalize(
        string docxPath,
        IReadOnlyDictionary<string, ResolvedStyle> resolvedRoleStyles,
        bool bodyOnly)
    {
        if (!resolvedRoleStyles.TryGetValue(StyleRoles.OrderedList, out var orderedStyle)
            || !resolvedRoleStyles.TryGetValue(StyleRoles.UnorderedList, out var unorderedStyle))
        {
            throw new WorkerCommandException("LIST_STYLE_MAPPING_MISSING", "Ordered and unordered list styles must be resolved before finalization.", 4, "openxml-finalize");
        }

        using var document = WordprocessingDocument.Open(docxPath, true);
        var mainPart = document.MainDocumentPart
            ?? throw new WorkerCommandException("DOCX_STRUCTURE_INVALID", "The generated DOCX has no main document part.", 4, "openxml-finalize");
        var body = mainPart.Document?.Body
            ?? throw new WorkerCommandException("DOCX_STRUCTURE_INVALID", "The generated DOCX has no document body.", 4, "openxml-finalize");
        var targetParagraphs = EnumerateTargetParagraphs(body, bodyOnly).ToArray();
        var numberingPart = mainPart.NumberingDefinitionsPart;
        var numbering = numberingPart?.Numbering;
        if (numbering is null)
        {
            var hasListParagraph = targetParagraphs.Any(paragraph =>
                paragraph.ParagraphProperties?.NumberingProperties is not null
                || RoleMarkerRegex.Matches(paragraph.InnerText).Any(match =>
                    match.Groups["role"].Value is StyleRoles.OrderedList or StyleRoles.UnorderedList));
            if (hasListParagraph)
            {
                throw new WorkerCommandException(
                    "DOCX_NUMBERING_MISSING",
                    "The generated DOCX has list paragraphs but no numbering part.",
                    4,
                    "openxml-finalize");
            }
            return new ListFinalizationResult(0, 0);
        }

        var nextNumberId = numbering.Elements<NumberingInstance>()
            .Select(instance => instance.NumberID?.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var nextAbstractNumberId = numbering.Elements<AbstractNum>()
            .Select(abstractNumber => abstractNumber.AbstractNumberId?.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var orderedStyleAbstract = ResolveStyleAbstractNumbering(mainPart, numbering, orderedStyle.StyleId);
        var unorderedStyleAbstract = ResolveStyleAbstractNumbering(mainPart, numbering, unorderedStyle.StyleId);
        var unorderedBulletBaseMarker = ResolveStyleFirstLineIndent(mainPart, unorderedStyle.StyleId);
        var templateAwareAbstracts = new Dictionary<int, int>();
        var normalizedBulletNumberIds = new Dictionary<int, int>();
        var activeOrderedByLevel = new Dictionary<int, OrderedContext>();
        string? lastListRole = null;
        var normalized = 0;
        var restarted = 0;

        foreach (var paragraph in targetParagraphs)
        {
            var markedRoles = RoleMarkerRegex.Matches(paragraph.InnerText)
                .Select(match => match.Groups["role"].Value)
                .ToHashSet(StringComparer.Ordinal);
            if (markedRoles.Contains(StyleRoles.Heading1)
                || markedRoles.Contains(StyleRoles.Heading2)
                || markedRoles.Contains(StyleRoles.Heading3)
                || markedRoles.Contains(StyleRoles.Heading4)
                || markedRoles.Contains(StyleRoles.Heading5)
                || markedRoles.Contains(StyleRoles.Heading6))
            {
                // Heading numbering is a separate multilevel-list contract.
                // Treating a numbered heading as a Markdown list paragraph
                // creates a fresh numId whenever body text separates headings.
                activeOrderedByLevel.Clear();
                lastListRole = null;
                continue;
            }

            var properties = paragraph.GetFirstChild<ParagraphProperties>();
            var numberingProperties = properties?.GetFirstChild<NumberingProperties>();
            var sourceNumberId = numberingProperties?.NumberingId?.Val?.Value;
            var level = numberingProperties?.NumberingLevelReference?.Val?.Value ?? 0;
            if (properties is null || numberingProperties is null || sourceNumberId is null)
            {
                var markedListRole = markedRoles.Contains(StyleRoles.OrderedList)
                    ? StyleRoles.OrderedList
                    : markedRoles.Contains(StyleRoles.UnorderedList)
                        ? StyleRoles.UnorderedList
                        : null;
                if (markedListRole is not null)
                {
                    // List-contained code has both a list-context marker and a more
                    // specific code marker. Preserve numbering state here; the role
                    // finalizer that follows will apply CodeBlock and remove markers.
                    var hasMoreSpecificRole = markedRoles.Contains(StyleRoles.CodeBlock)
                        || markedRoles.Contains(StyleRoles.Caption)
                        || markedRoles.Contains(StyleRoles.TableCaption)
                        || markedRoles.Contains(StyleRoles.FigureImage)
                        || markedRoles.Contains(StyleRoles.Table)
                        || markedRoles.Contains(StyleRoles.Admonition);
                    if (!hasMoreSpecificRole)
                    {
                        if (properties is null)
                        {
                            properties = new ParagraphProperties();
                            paragraph.PrependChild(properties);
                        }
                        var continuationStyle = markedListRole == StyleRoles.OrderedList ? orderedStyle : unorderedStyle;
                        SetParagraphStyle(properties, continuationStyle.StyleId);
                        ClearDirectParagraphFormatting(properties);
                        if (markedListRole == StyleRoles.OrderedList && orderedStyleAbstract is not null)
                        {
                            SuppressInheritedStyleNumbering(properties);
                        }
                        normalized++;
                    }
                    lastListRole = markedListRole;
                    continue;
                }
                if (properties is not null && lastListRole is not null && IsListContinuation(properties))
                {
                    var continuationStyle = lastListRole == StyleRoles.OrderedList ? orderedStyle : unorderedStyle;
                    SetParagraphStyle(properties, continuationStyle.StyleId);
                    ClearDirectParagraphFormatting(properties);
                    if (lastListRole == StyleRoles.OrderedList && orderedStyleAbstract is not null)
                    {
                        SuppressInheritedStyleNumbering(properties);
                    }
                    normalized++;
                    continue;
                }
                activeOrderedByLevel.Clear();
                lastListRole = null;
                continue;
            }

            var listInfo = ResolveListInfo(numbering, sourceNumberId.Value, level);
            if (listInfo is null)
            {
                activeOrderedByLevel.Clear();
                lastListRole = null;
                continue;
            }

            var isBullet = listInfo.NumberFormat == NumberFormatValues.Bullet;
            if (isBullet)
            {
                RemoveAtOrDeeper(activeOrderedByLevel, level);
                var parentOrderedContext = activeOrderedByLevel
                    .Where(pair => pair.Key < level && pair.Value.SourceNumberId == sourceNumberId.Value)
                    .OrderByDescending(pair => pair.Key)
                    .Select(pair => pair.Value)
                    .FirstOrDefault();
                if (parentOrderedContext is not null)
                {
                    // A bullet nested under an ordered level is part of the same
                    // native multilevel list. Keep every level on the restarted
                    // numId so Word does not detach the child list hierarchy.
                    numberingProperties.NumberingId!.Val = parentOrderedContext.RestartedNumberId;
                }
                else
                {
                    var abstractNumberId = GetOrCreateTemplateAwareAbstractNumbering(
                        numbering,
                        listInfo.AbstractNumberId,
                        orderedStyleAbstract,
                        unorderedStyleAbstract,
                        unorderedBulletBaseMarker,
                        templateAwareAbstracts,
                        ref nextAbstractNumberId);
                    numberingProperties.NumberingId!.Val = GetOrCreateNormalizedBulletNumbering(
                        numbering,
                        sourceNumberId.Value,
                        abstractNumberId,
                        normalizedBulletNumberIds,
                        ref nextNumberId);
                }
                SetParagraphStyle(properties, unorderedStyle.StyleId);
                lastListRole = StyleRoles.UnorderedList;
            }
            else
            {
                RemoveDeeper(activeOrderedByLevel, level);
                if (!activeOrderedByLevel.TryGetValue(level, out var context)
                    || context.SourceNumberId != sourceNumberId.Value)
                {
                    context = activeOrderedByLevel
                        .Where(pair => pair.Key < level && pair.Value.SourceNumberId == sourceNumberId.Value)
                        .OrderByDescending(pair => pair.Key)
                        .Select(pair => pair.Value)
                        .FirstOrDefault();
                    if (context is null)
                    {
                        var restartedNumberId = nextNumberId++;
                        var abstractNumberId = GetOrCreateTemplateAwareAbstractNumbering(
                            numbering,
                            listInfo.AbstractNumberId,
                            orderedStyleAbstract,
                            unorderedStyleAbstract,
                            unorderedBulletBaseMarker,
                            templateAwareAbstracts,
                            ref nextAbstractNumberId);
                        AppendRestartedNumbering(numbering, restartedNumberId, abstractNumberId, level, listInfo.StartValue);
                        context = new OrderedContext(sourceNumberId.Value, restartedNumberId);
                        restarted++;
                    }
                    activeOrderedByLevel[level] = context;
                }
                numberingProperties.NumberingId!.Val = context.RestartedNumberId;
                SetParagraphStyle(properties, orderedStyle.StyleId);
                lastListRole = StyleRoles.OrderedList;
            }

            ClearDirectParagraphFormatting(properties);
            normalized++;
        }

        mainPart.Document!.Save();
        numbering.Save();
        return new ListFinalizationResult(normalized, restarted);
    }

    private static AbstractNum? ResolveStyleAbstractNumbering(
        MainDocumentPart mainPart,
        Numbering numbering,
        string styleId)
    {
        var style = mainPart.StyleDefinitionsPart?.Styles?
            .Elements<Style>()
            .FirstOrDefault(candidate => candidate.StyleId?.Value == styleId);
        var styleNumberId = style?
            .GetFirstChild<StyleParagraphProperties>()?
            .GetFirstChild<NumberingProperties>()?
            .NumberingId?
            .Val?
            .Value;
        if (styleNumberId is null or 0)
        {
            return null;
        }

        var instance = numbering.Elements<NumberingInstance>()
            .FirstOrDefault(candidate => candidate.NumberID?.Value == styleNumberId.Value);
        var abstractNumberId = instance?.AbstractNumId?.Val?.Value;
        return abstractNumberId is null
            ? null
            : numbering.Elements<AbstractNum>()
                .FirstOrDefault(candidate => candidate.AbstractNumberId?.Value == abstractNumberId.Value);
    }

    private static int GetOrCreateTemplateAwareAbstractNumbering(
        Numbering numbering,
        int sourceAbstractNumberId,
        AbstractNum? orderedStyleAbstract,
        AbstractNum? unorderedStyleAbstract,
        int unorderedBulletBaseMarker,
        IDictionary<int, int> cache,
        ref int nextAbstractNumberId)
    {
        if (cache.TryGetValue(sourceAbstractNumberId, out var existing))
        {
            return existing;
        }

        var sourceAbstract = numbering.Elements<AbstractNum>()
            .FirstOrDefault(candidate => candidate.AbstractNumberId?.Value == sourceAbstractNumberId);
        if (sourceAbstract is null)
        {
            return sourceAbstractNumberId;
        }

        var derived = (AbstractNum)sourceAbstract.CloneNode(true);
        var derivedId = nextAbstractNumberId++;
        derived.AbstractNumberId = derivedId;
        AssignDerivedNumberingIdentity(derived, sourceAbstractNumberId, derivedId);
        foreach (var sourceLevel in derived.Elements<Level>().ToArray())
        {
            var levelIndex = sourceLevel.LevelIndex?.Value;
            if (levelIndex is null)
            {
                continue;
            }

            var isBullet = sourceLevel.NumberingFormat?.Val?.Value == NumberFormatValues.Bullet;
            Level? replacement;
            if (isBullet)
            {
                replacement = ResolveBulletLevel(
                    unorderedStyleAbstract,
                    levelIndex.Value,
                    unorderedBulletBaseMarker);
            }
            else
            {
                replacement = orderedStyleAbstract?.Elements<Level>()
                    .FirstOrDefault(candidate => candidate.LevelIndex?.Value == levelIndex.Value);
            }
            if (replacement is null)
            {
                continue;
            }

            var sourceStart = sourceLevel.StartNumberingValue?.Val?.Value;
            replacement = (Level)replacement.CloneNode(true);
            replacement.LevelIndex = levelIndex.Value;
            replacement.RemoveAllChildren<ParagraphStyleIdInLevel>();
            if (sourceStart is not null)
            {
                replacement.RemoveAllChildren<StartNumberingValue>();
                replacement.PrependChild(new StartNumberingValue { Val = sourceStart.Value });
            }
            sourceLevel.InsertBeforeSelf(replacement);
            sourceLevel.Remove();
        }

        var firstInstance = numbering.Elements<NumberingInstance>().FirstOrDefault();
        if (firstInstance is null)
        {
            numbering.Append(derived);
        }
        else
        {
            numbering.InsertBefore(derived, firstInstance);
        }
        cache[sourceAbstractNumberId] = derivedId;
        return derivedId;
    }

    private static int GetOrCreateNormalizedBulletNumbering(
        Numbering numbering,
        int sourceNumberId,
        int abstractNumberId,
        IDictionary<int, int> cache,
        ref int nextNumberId)
    {
        if (cache.TryGetValue(sourceNumberId, out var existing))
        {
            return existing;
        }

        var source = numbering.Elements<NumberingInstance>()
            .First(candidate => candidate.NumberID?.Value == sourceNumberId);
        var normalized = (NumberingInstance)source.CloneNode(true);
        var normalizedNumberId = nextNumberId++;
        normalized.NumberID = normalizedNumberId;
        normalized.AbstractNumId!.Val = abstractNumberId;
        numbering.Append(normalized);
        cache[sourceNumberId] = normalizedNumberId;
        return normalizedNumberId;
    }

    private static Level ResolveBulletLevel(
        AbstractNum? unorderedStyleAbstract,
        int levelIndex,
        int baseMarkerPosition)
    {
        var templateLevel = unorderedStyleAbstract?.Elements<Level>()
            .FirstOrDefault(candidate =>
                candidate.LevelIndex?.Value == levelIndex
                && candidate.NumberingFormat?.Val?.Value == NumberFormatValues.Bullet);
        if (templateLevel is null && levelIndex == 0)
        {
            templateLevel = unorderedStyleAbstract?.Elements<Level>()
                .FirstOrDefault(candidate => candidate.NumberingFormat?.Val?.Value == NumberFormatValues.Bullet);
        }
        return templateLevel ?? CreateWordGalleryBulletLevel(levelIndex, baseMarkerPosition);
    }

    private static Level CreateWordGalleryBulletLevel(int levelIndex, int baseMarkerPosition)
    {
        var glyphs = new[] { "\uF06C", "\uF06E", "\uF075" };
        // Word's Increase Indent command advances a manually inserted bullet
        // by 11 pt per level. The marker starts at a positive first-line
        // indent from the target paragraph style (for example, 420 twips).
        const int levelStep = 220;
        const int hanging = 440;
        var markerPosition = Math.Max(0, baseMarkerPosition) + (levelIndex * levelStep);
        var textPosition = markerPosition + hanging;
        return new Level(
            new StartNumberingValue { Val = 1 },
            new NumberingFormat { Val = NumberFormatValues.Bullet },
            new LevelSuffix { Val = LevelSuffixValues.Tab },
            new LevelText { Val = glyphs[levelIndex % glyphs.Length] },
            new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(
                new Tabs(new TabStop { Val = TabStopValues.Number, Position = textPosition }),
                new Indentation { Left = textPosition.ToString(), Hanging = hanging.ToString() }),
            new NumberingSymbolRunProperties(
                new RunFonts { Ascii = "Wingdings", HighAnsi = "Wingdings" }))
        {
            LevelIndex = levelIndex,
        };
    }

    private static int ResolveStyleFirstLineIndent(MainDocumentPart mainPart, string styleId)
    {
        var styles = mainPart.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            return 0;
        }

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

            var indentation = style.GetFirstChild<StyleParagraphProperties>()?
                .GetFirstChild<Indentation>();
            if (indentation is not null)
            {
                if (int.TryParse(indentation.FirstLine?.Value, out var firstLine) && firstLine > 0)
                {
                    return firstLine;
                }
                if (!string.IsNullOrWhiteSpace(indentation.Hanging?.Value))
                {
                    return 0;
                }
            }

            currentStyleId = style.GetFirstChild<BasedOn>()?.Val?.Value ?? string.Empty;
        }
        return 0;
    }

    private static void AssignDerivedNumberingIdentity(
        AbstractNum derived,
        int sourceAbstractNumberId,
        int derivedAbstractNumberId)
    {
        var identity = unchecked(
            0x4D320000u
            ^ ((uint)sourceAbstractNumberId * 0x45D9F3Bu)
            ^ ((uint)derivedAbstractNumberId * 0x119DE1F3u))
            .ToString("X8");
        if (derived.GetFirstChild<Nsid>() is { } nsid)
        {
            nsid.Val = identity;
        }
        if (derived.GetFirstChild<TemplateCode>() is { } templateCode)
        {
            templateCode.Val = identity;
        }
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

    private static ListInfo? ResolveListInfo(Numbering numbering, int numberId, int levelIndex)
    {
        var instance = numbering.Elements<NumberingInstance>()
            .FirstOrDefault(candidate => candidate.NumberID?.Value == numberId);
        var abstractNumberId = instance?.AbstractNumId?.Val?.Value;
        if (abstractNumberId is null)
        {
            return null;
        }
        var abstractNumber = numbering.Elements<AbstractNum>()
            .FirstOrDefault(candidate => candidate.AbstractNumberId?.Value == abstractNumberId.Value);
        var level = abstractNumber?.Elements<Level>()
            .FirstOrDefault(candidate => candidate.LevelIndex?.Value == levelIndex)
            ?? abstractNumber?.Elements<Level>().FirstOrDefault(candidate => candidate.LevelIndex?.Value == 0);
        var format = level?.NumberingFormat?.Val?.Value;
        var levelOverride = instance?.Elements<LevelOverride>().FirstOrDefault(candidate => candidate.LevelIndex?.Value == levelIndex);
        var startValue = levelOverride?.StartOverrideNumberingValue?.Val?.Value
            ?? levelOverride?.Level?.StartNumberingValue?.Val?.Value
            ?? level?.StartNumberingValue?.Val?.Value
            ?? 1;
        return format is null ? null : new ListInfo(abstractNumberId.Value, format.Value, startValue);
    }

    private static void AppendRestartedNumbering(Numbering numbering, int numberId, int abstractNumberId, int level, int startValue)
    {
        var instance = new NumberingInstance { NumberID = numberId };
        instance.Append(new AbstractNumId { Val = abstractNumberId });
        var levelOverride = new LevelOverride { LevelIndex = level };
        levelOverride.Append(new StartOverrideNumberingValue { Val = startValue });
        instance.Append(levelOverride);
        numbering.Append(instance);
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

    private static void SuppressInheritedStyleNumbering(ParagraphProperties properties)
    {
        var numberingProperties = properties.GetFirstChild<NumberingProperties>();
        if (numberingProperties is null)
        {
            numberingProperties = new NumberingProperties();
            var paragraphStyle = properties.GetFirstChild<ParagraphStyleId>();
            if (paragraphStyle is null)
            {
                properties.PrependChild(numberingProperties);
            }
            else
            {
                properties.InsertAfter(numberingProperties, paragraphStyle);
            }
        }
        numberingProperties.RemoveAllChildren();
        numberingProperties.Append(new NumberingId { Val = 0 });
    }

    private static bool IsListContinuation(ParagraphProperties properties)
    {
        var styleId = properties.ParagraphStyleId?.Val?.Value ?? string.Empty;
        return styleId.Contains("manual-body-item", StringComparison.OrdinalIgnoreCase)
            || styleId.Contains("manual-body-list", StringComparison.OrdinalIgnoreCase)
            || styleId.Contains("manual-body-ordered", StringComparison.OrdinalIgnoreCase)
            || styleId.Contains("manual-body-unordered", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveAtOrDeeper(Dictionary<int, OrderedContext> active, int level)
    {
        foreach (var key in active.Keys.Where(key => key >= level).ToArray())
        {
            active.Remove(key);
        }
    }

    private static void RemoveDeeper(Dictionary<int, OrderedContext> active, int level)
    {
        foreach (var key in active.Keys.Where(key => key > level).ToArray())
        {
            active.Remove(key);
        }
    }

    private sealed record OrderedContext(int SourceNumberId, int RestartedNumberId);
    private sealed record ListInfo(int AbstractNumberId, NumberFormatValues NumberFormat, int StartValue);
}
