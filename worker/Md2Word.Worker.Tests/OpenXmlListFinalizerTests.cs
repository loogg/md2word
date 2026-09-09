using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlListFinalizerTests
{
    private static readonly IReadOnlyDictionary<string, ResolvedStyle> RoleStyles =
        new Dictionary<string, ResolvedStyle>
        {
            [StyleRoles.OrderedList] = new("ExampleOrdered", "示例 有序列项"),
            [StyleRoles.UnorderedList] = new("ExampleBody", "示例 正文"),
        };

    [Fact]
    public void RebindsNativeListsClearsDirectFormattingRestartsSeparatedBlocksAndPreservesStart()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.CreateListDocument();

        var result = new OpenXmlListFinalizer().Finalize(path, RoleStyles, bodyOnly: true);

        Assert.Equal(4, result.NormalizedParagraphs);
        Assert.Equal(2, result.RestartedOrderedLists);
        using var document = WordprocessingDocument.Open(path, false);
        var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToArray();
        var numbering = document.MainDocumentPart.NumberingDefinitionsPart!.Numbering!;
        var byText = paragraphs.Where(paragraph => paragraph.InnerText.Length > 0).ToDictionary(paragraph => paragraph.InnerText, StringComparer.Ordinal);
        var first = byText["ordered-a"];
        var second = byText["ordered-b"];
        var separate = byText["ordered-new-block"];
        var bullet = byText["bullet"];

        Assert.Equal("ExampleOrdered", Style(first));
        Assert.Equal("ExampleOrdered", Style(separate));
        Assert.Equal("ExampleBody", Style(bullet));
        var normalizedBulletLevel = ResolveLevel(numbering, bullet);
        Assert.Equal("\uF06C", normalizedBulletLevel.LevelText?.Val?.Value);
        Assert.Equal("Wingdings", normalizedBulletLevel.NumberingSymbolRunProperties?.RunFonts?.Ascii?.Value);
        Assert.Equal("440", normalizedBulletLevel.PreviousParagraphProperties?.Indentation?.Left?.Value);
        Assert.Equal("440", normalizedBulletLevel.PreviousParagraphProperties?.Indentation?.Hanging?.Value);
        Assert.NotEqual(1, NumId(bullet));
        Assert.NotNull(NumId(first));
        Assert.Equal(NumId(first), NumId(second));
        Assert.NotEqual(NumId(first), NumId(separate));
        Assert.Null(first.ParagraphProperties!.SpacingBetweenLines);
        Assert.NotNull(first.ParagraphProperties.NumberingProperties);
        Assert.Equal("Imported", Style(byText["outside-before"]));
        Assert.NotNull(byText["outside-before"].ParagraphProperties!.SpacingBetweenLines);

        var restartedIds = new[] { NumId(first)!.Value, NumId(separate)!.Value };
        foreach (var numberId in restartedIds)
        {
            var instance = numbering.Elements<NumberingInstance>().Single(item => item.NumberID?.Value == numberId);
            Assert.Equal(3, instance.GetFirstChild<LevelOverride>()!.StartOverrideNumberingValue!.Val!.Value);
        }
    }

    [Fact]
    public void PreservesOuterOrderedContextAcrossNestedBulletAndLooseContinuation()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.CreateListDocument(includeNestedMixed: true, includeContinuation: true);

        new OpenXmlListFinalizer().Finalize(path, RoleStyles, bodyOnly: true);

        using var document = WordprocessingDocument.Open(path, false);
        var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Where(paragraph => paragraph.InnerText.Length > 0)
            .ToDictionary(paragraph => paragraph.InnerText);
        Assert.Equal(NumId(paragraphs["ordered-a"]), NumId(paragraphs["ordered-b"]));
        Assert.Equal("ExampleBody", Style(paragraphs["nested-bullet"]));
        Assert.Equal("ExampleOrdered", Style(paragraphs["ordered continuation"]));
        Assert.Null(paragraphs["ordered continuation"].ParagraphProperties!.NumberingProperties);
        Assert.Null(paragraphs["ordered continuation"].ParagraphProperties!.Indentation);
    }

    [Fact]
    public void UsesOrderedStyleNumberingAppearanceWhilePreservingMarkdownRestartsAndMixedLevels()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.CreateListDocument(includeNestedMixed: true, includeContinuation: true);
        using (var document = WordprocessingDocument.Open(path, true))
        {
            var mainPart = document.MainDocumentPart!;
            var orderedStyle = mainPart.StyleDefinitionsPart!.Styles!
                .Elements<Style>()
                .Single(style => style.StyleId?.Value == "ExampleOrdered");
            orderedStyle.Append(new StyleParagraphProperties(
                new NumberingProperties(new NumberingId { Val = 9 })));
            mainPart.StyleDefinitionsPart.Styles.Save();

            var numbering = mainPart.NumberingDefinitionsPart!.Numbering!;
            var sourceAbstract = numbering.Elements<AbstractNum>()
                .Single(candidate => candidate.AbstractNumberId?.Value == 2);
            sourceAbstract.PrependChild(new Nsid { Val = "11111111" });
            sourceAbstract.InsertAfter(
                new TemplateCode { Val = "11111111" },
                sourceAbstract.GetFirstChild<Nsid>());
            var templateAbstract = new AbstractNum { AbstractNumberId = 9 };
            templateAbstract.Append(
                TemplateLevel(0, NumberFormatValues.Decimal, "%1）", 780, 360),
                TemplateLevel(1, NumberFormatValues.LowerLetter, "%2)", 1260, 420));
            numbering.InsertBefore(templateAbstract, numbering.Elements<NumberingInstance>().First());
            numbering.Append(new NumberingInstance(new AbstractNumId { Val = 9 }) { NumberID = 9 });
            numbering.Save();
        }

        new OpenXmlListFinalizer().Finalize(path, RoleStyles, bodyOnly: true);

        using var reopened = WordprocessingDocument.Open(path, false);
        var main = reopened.MainDocumentPart!;
        var numberingAfter = main.NumberingDefinitionsPart!.Numbering!;
        var paragraphs = main.Document!.Body!.Elements<Paragraph>()
            .Where(paragraph => paragraph.InnerText.Length > 0)
            .ToDictionary(paragraph => paragraph.InnerText, StringComparer.Ordinal);
        var first = paragraphs["ordered-a"];
        var nestedBullet = paragraphs["nested-bullet"];
        var separate = paragraphs["ordered-new-block"];
        var continuation = paragraphs["ordered continuation"];

        var effectiveOrderedLevel = ResolveLevel(numberingAfter, first);
        var derivedAbstract = numberingAfter.Elements<AbstractNum>()
            .Single(candidate => candidate.AbstractNumberId?.Value == ResolveAbstractNumberId(numberingAfter, first));
        Assert.Equal("%1）", effectiveOrderedLevel.LevelText?.Val?.Value);
        Assert.Equal(NumberFormatValues.Decimal, effectiveOrderedLevel.NumberingFormat?.Val?.Value);
        Assert.Equal("780", effectiveOrderedLevel.PreviousParagraphProperties?.Indentation?.Left?.Value);
        Assert.Equal("360", effectiveOrderedLevel.PreviousParagraphProperties?.Indentation?.Hanging?.Value);
        Assert.Null(effectiveOrderedLevel.ParagraphStyleIdInLevel);
        var effectiveBulletLevel = ResolveLevel(numberingAfter, nestedBullet);
        Assert.Equal(NumberFormatValues.Bullet, effectiveBulletLevel.NumberingFormat?.Val?.Value);
        Assert.Equal("\uF06E", effectiveBulletLevel.LevelText?.Val?.Value);
        Assert.Equal("Wingdings", effectiveBulletLevel.NumberingSymbolRunProperties?.RunFonts?.Ascii?.Value);
        Assert.Equal("660", effectiveBulletLevel.PreviousParagraphProperties?.Indentation?.Left?.Value);
        Assert.Equal("440", effectiveBulletLevel.PreviousParagraphProperties?.Indentation?.Hanging?.Value);
        Assert.Equal(3, ResolveStart(numberingAfter, first));
        Assert.NotEqual(NumId(first), NumId(separate));
        Assert.NotEqual(2, ResolveAbstractNumberId(numberingAfter, first));
        Assert.NotEqual(9, ResolveAbstractNumberId(numberingAfter, first));
        Assert.NotEqual("11111111", derivedAbstract.GetFirstChild<Nsid>()?.Val?.Value);
        Assert.NotEqual("11111111", derivedAbstract.GetFirstChild<TemplateCode>()?.Val?.Value);
        Assert.Equal("ExampleOrdered", Style(continuation));
        Assert.Equal(0, NumId(continuation));
        Assert.Null(continuation.ParagraphProperties!.NumberingProperties!.NumberingLevelReference);
    }

    [Fact]
    public void OffsetsGalleryBulletsFromTargetStyleFirstLineUsingWordIndentSteps()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.CreateListAcceptanceDocument();
        using (var document = WordprocessingDocument.Open(path, true))
        {
            var styles = document.MainDocumentPart!.StyleDefinitionsPart!.Styles!;
            var bodyStyle = styles.Elements<Style>()
                .Single(style => style.StyleId?.Value == "ExampleBody");
            bodyStyle.Append(new StyleParagraphProperties(
                new Indentation { FirstLine = "420" }));
            styles.Save();
        }

        new OpenXmlListFinalizer().Finalize(path, RoleStyles, bodyOnly: true);

        using var reopened = WordprocessingDocument.Open(path, false);
        var paragraphs = reopened.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Where(paragraph => paragraph.InnerText.Length > 0)
            .ToDictionary(paragraph => paragraph.InnerText, StringComparer.Ordinal);
        var numbering = reopened.MainDocumentPart.NumberingDefinitionsPart!.Numbering!;
        var rootLevel = ResolveLevel(numbering, paragraphs["bullet-root-a"]);
        var thirdLevel = ResolveLevel(numbering, paragraphs["bullet-third-level"]);
        Assert.Equal("860", rootLevel.PreviousParagraphProperties?.Indentation?.Left?.Value);
        Assert.Equal("1300", thirdLevel.PreviousParagraphProperties?.Indentation?.Left?.Value);
        Assert.Equal("440", rootLevel.PreviousParagraphProperties?.Indentation?.Hanging?.Value);
        Assert.Equal("440", thirdLevel.PreviousParagraphProperties?.Indentation?.Hanging?.Value);
    }

    [Fact]
    public void NoListContentDoesNotRequireNumberingPart()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.CreateTemplate(Array.Empty<(string Id, string Name, string? Aliases)>());

        var result = new OpenXmlListFinalizer().Finalize(path, RoleStyles, bodyOnly: true);

        Assert.Equal(0, result.NormalizedParagraphs);
        Assert.Equal(0, result.RestartedOrderedLists);
    }

    [Fact]
    public void ListMarkerWithoutNumberingPartStillFailsClosed()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.CreateTemplate(Array.Empty<(string Id, string Name, string? Aliases)>());
        using (var document = WordprocessingDocument.Open(path, true))
        {
            var body = document.MainDocumentPart!.Document!.Body!;
            var endBookmarkParagraph = body.Elements<Paragraph>()
                .Single(paragraph => paragraph.Descendants<BookmarkStart>()
                    .Any(bookmark => bookmark.Name?.Value == TemplateValidationService.BodyEndBookmark));
            endBookmarkParagraph.InsertBeforeSelf(new Paragraph(new Run(new Text(
                "__MD2WORD_ROLE_0123456789abcdef0123456789abcdef_ordered-list__item"))));
            document.MainDocumentPart.Document.Save();
        }

        var exception = Assert.Throws<WorkerCommandException>(() =>
            new OpenXmlListFinalizer().Finalize(path, RoleStyles, bodyOnly: true));

        Assert.Equal("DOCX_NUMBERING_MISSING", exception.Code);
    }

    [Fact]
    public void PreservesHeadingNumberingAcrossInterleavedBodyParagraphs()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.CreateListDocument();
        using (var document = WordprocessingDocument.Open(path, true))
        {
            var body = document.MainDocumentPart!.Document!.Body!;
            var endBookmarkParagraph = body.Elements<Paragraph>()
                .Single(paragraph => paragraph.Descendants<BookmarkStart>()
                    .Any(bookmark => bookmark.Name?.Value == TemplateValidationService.BodyEndBookmark));
            endBookmarkParagraph.InsertBeforeSelf(
                HeadingParagraph("h1", "H1", "First heading", 2, 0));
            endBookmarkParagraph.InsertBeforeSelf(new Paragraph(new Run(new Text("Interleaved body paragraph"))));
            endBookmarkParagraph.InsertBeforeSelf(
                HeadingParagraph("h2", "H2", "Second heading", 2, 1));
            document.MainDocumentPart.Document.Save();
        }

        new OpenXmlListFinalizer().Finalize(path, RoleStyles, bodyOnly: true);

        using var reopened = WordprocessingDocument.Open(path, false);
        var headings = reopened.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Where(paragraph => paragraph.InnerText.Contains("heading", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, headings.Length);
        Assert.Equal([2], headings.Select(NumId).Distinct().ToArray());
        Assert.Equal("H1", Style(headings[0]));
        Assert.Equal("H2", Style(headings[1]));
        Assert.All(headings, paragraph => Assert.NotNull(paragraph.ParagraphProperties!.SpacingBetweenLines));
    }

    private static Paragraph HeadingParagraph(
        string role,
        string styleId,
        string text,
        int numberId,
        int level) => new(
            new ParagraphProperties(
                new ParagraphStyleId { Val = styleId },
                new NumberingProperties(
                    new NumberingLevelReference { Val = level },
                    new NumberingId { Val = numberId }),
                new SpacingBetweenLines { Before = "240" }),
            new Run(new Text($"__MD2WORD_ROLE_0123456789abcdef0123456789abcdef_{role}__{text}")));

    private static string? Style(Paragraph paragraph) => paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
    private static int? NumId(Paragraph paragraph) => paragraph.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value;

    private static Level TemplateLevel(
        int level,
        NumberFormatValues format,
        string text,
        int left,
        int hanging) => new(
            new StartNumberingValue { Val = 1 },
            new NumberingFormat { Val = format },
            new ParagraphStyleIdInLevel { Val = "ExampleOrdered" },
            new LevelText { Val = text },
            new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(
                new Indentation { Left = left.ToString(), Hanging = hanging.ToString() }))
        {
            LevelIndex = level,
        };

    private static Level ResolveLevel(Numbering numbering, Paragraph paragraph)
    {
        var properties = paragraph.ParagraphProperties!.NumberingProperties!;
        var abstractNumberId = ResolveAbstractNumberId(numbering, paragraph);
        var level = properties.NumberingLevelReference!.Val!.Value;
        return numbering.Elements<AbstractNum>()
            .Single(candidate => candidate.AbstractNumberId?.Value == abstractNumberId)
            .Elements<Level>()
            .Single(candidate => candidate.LevelIndex?.Value == level);
    }

    private static int ResolveAbstractNumberId(Numbering numbering, Paragraph paragraph)
    {
        var numberId = NumId(paragraph)!.Value;
        return numbering.Elements<NumberingInstance>()
            .Single(candidate => candidate.NumberID?.Value == numberId)
            .AbstractNumId!
            .Val!
            .Value;
    }

    private static int ResolveStart(Numbering numbering, Paragraph paragraph)
    {
        var properties = paragraph.ParagraphProperties!.NumberingProperties!;
        var numberId = properties.NumberingId!.Val!.Value;
        var level = properties.NumberingLevelReference!.Val!.Value;
        var instance = numbering.Elements<NumberingInstance>()
            .Single(candidate => candidate.NumberID?.Value == numberId);
        return instance.Elements<LevelOverride>()
                   .FirstOrDefault(candidate => candidate.LevelIndex?.Value == level)?
                   .StartOverrideNumberingValue?
                   .Val?
                   .Value
               ?? ResolveLevel(numbering, paragraph).StartNumberingValue?.Val?.Value
               ?? 1;
    }
}
