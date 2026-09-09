using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlListAcceptanceMatrixTests
{
    private static readonly IReadOnlyDictionary<string, ResolvedStyle> RoleStyles =
        new Dictionary<string, ResolvedStyle>
        {
            [StyleRoles.OrderedList] = new("ExampleOrdered", "示例 有序列项"),
            [StyleRoles.UnorderedList] = new("ExampleBody", "示例 正文"),
            [StyleRoles.CodeBlock] = new("Code", "CodeBlock"),
        };

    [Fact]
    public void PreservesNativeNumberingAcrossLooseEmbeddedAndThreeLevelMixedLists()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.CreateListAcceptanceDocument();

        var result = new OpenXmlFinalizationPipeline().Finalize(path, RoleStyles, bodyOnly: true);

        Assert.Equal(14, result.Lists.NormalizedParagraphs);
        Assert.Equal(3, result.Lists.RestartedOrderedLists);
        Assert.Equal(5, result.Roles.MarkersRemoved);
        Assert.Equal(4, result.Roles.StylesApplied);

        using var document = WordprocessingDocument.Open(path, false);
        var mainPart = document.MainDocumentPart!;
        var numbering = mainPart.NumberingDefinitionsPart!.Numbering!;
        var paragraphs = mainPart.Document!.Body!.Elements<Paragraph>()
            .Where(paragraph => paragraph.InnerText.Length > 0)
            .ToDictionary(paragraph => paragraph.InnerText, StringComparer.Ordinal);

        var matrix = new[]
        {
            new ExpectedListParagraph("ordered-root-a", "ExampleOrdered", NumberFormatValues.Decimal, 0),
            new ExpectedListParagraph("ordered-nested-bullet", "ExampleBody", NumberFormatValues.Bullet, 1),
            new ExpectedListParagraph("ordered-third-level", "ExampleOrdered", NumberFormatValues.LowerLetter, 2),
            new ExpectedListParagraph("ordered-nested-bullet-return", "ExampleBody", NumberFormatValues.Bullet, 1),
            new ExpectedListParagraph("ordered-root-b", "ExampleOrdered", NumberFormatValues.Decimal, 0),
            new ExpectedListParagraph("ordered-restart-four", "ExampleOrdered", NumberFormatValues.Decimal, 0),
            new ExpectedListParagraph("bullet-root-a", "ExampleBody", NumberFormatValues.Bullet, 0),
            new ExpectedListParagraph("bullet-nested-ordered-a", "ExampleOrdered", NumberFormatValues.Decimal, 1),
            new ExpectedListParagraph("bullet-third-level", "ExampleBody", NumberFormatValues.Bullet, 2),
            new ExpectedListParagraph("bullet-nested-ordered-b", "ExampleOrdered", NumberFormatValues.Decimal, 1),
            new ExpectedListParagraph("bullet-root-b", "ExampleBody", NumberFormatValues.Bullet, 0),
        };

        foreach (var expected in matrix)
        {
            var paragraph = paragraphs[expected.Text];
            var numberingProperties = AssertNumbering(paragraph);
            Assert.Equal(expected.StyleId, Style(paragraph));
            Assert.Equal(expected.Level, numberingProperties.NumberingLevelReference!.Val!.Value);
            Assert.Equal(expected.Format, ResolveNumberFormat(numbering, numberingProperties));
            Assert.True(numberingProperties.NumberingId!.Val!.Value > 0);
        }

        var orderedRootNumberId = NumId(paragraphs["ordered-root-a"]);
        Assert.Equal(orderedRootNumberId, NumId(paragraphs["ordered-nested-bullet"]));
        Assert.Equal(orderedRootNumberId, NumId(paragraphs["ordered-third-level"]));
        Assert.Equal(orderedRootNumberId, NumId(paragraphs["ordered-root-b"]));

        var restartedNumberId = NumId(paragraphs["ordered-restart-four"]);
        Assert.NotEqual(orderedRootNumberId, restartedNumberId);
        Assert.Equal(4, ResolveStart(numbering, AssertNumbering(paragraphs["ordered-root-a"])));
        Assert.Equal(4, ResolveStart(numbering, AssertNumbering(paragraphs["ordered-restart-four"])));

        var nestedOrderedNumberId = NumId(paragraphs["bullet-nested-ordered-a"]);
        Assert.Equal(nestedOrderedNumberId, NumId(paragraphs["bullet-third-level"]));
        Assert.Equal(nestedOrderedNumberId, NumId(paragraphs["bullet-nested-ordered-b"]));
        Assert.Equal(2, ResolveStart(numbering, AssertNumbering(paragraphs["bullet-nested-ordered-a"])));

        foreach (var text in new[]
        {
            "ordered loose continuation a",
            "ordered loose continuation b",
            "ordered embedded image",
        })
        {
            var paragraph = paragraphs[text];
            Assert.Equal("ExampleOrdered", Style(paragraph));
            Assert.Null(paragraph.ParagraphProperties!.NumberingProperties);
            Assert.Null(paragraph.ParagraphProperties.Indentation);
            Assert.Null(paragraph.ParagraphProperties.SpacingBetweenLines);
        }

        var code = paragraphs["ordered embedded code"];
        Assert.Equal("Code", Style(code));
        Assert.Null(code.ParagraphProperties!.NumberingProperties);
        Assert.Null(code.ParagraphProperties.Indentation);
        Assert.DoesNotContain("__MD2WORD_ROLE_", code.InnerText, StringComparison.Ordinal);

        Assert.Equal("Imported", Style(paragraphs["outside-ordered"]));
        Assert.Equal("Imported", Style(paragraphs["outside-bullet"]));
        Assert.Equal(20, NumId(paragraphs["outside-bullet"]));
    }

    private static NumberingProperties AssertNumbering(Paragraph paragraph)
    {
        var numbering = paragraph.ParagraphProperties?.NumberingProperties;
        Assert.NotNull(numbering);
        Assert.NotNull(numbering!.NumberingId?.Val);
        Assert.NotNull(numbering.NumberingLevelReference?.Val);
        return numbering;
    }

    private static NumberFormatValues ResolveNumberFormat(Numbering numbering, NumberingProperties properties) =>
        ResolveLevel(numbering, properties).NumberingFormat!.Val!.Value;

    private static int ResolveStart(Numbering numbering, NumberingProperties properties)
    {
        var numberId = properties.NumberingId!.Val!.Value;
        var levelIndex = properties.NumberingLevelReference!.Val!.Value;
        var instance = numbering.Elements<NumberingInstance>()
            .Single(candidate => candidate.NumberID?.Value == numberId);
        var levelOverride = instance.Elements<LevelOverride>()
            .FirstOrDefault(candidate => candidate.LevelIndex?.Value == levelIndex);
        return levelOverride?.StartOverrideNumberingValue?.Val?.Value
            ?? levelOverride?.Level?.StartNumberingValue?.Val?.Value
            ?? ResolveLevel(numbering, properties).StartNumberingValue?.Val?.Value
            ?? 1;
    }

    private static Level ResolveLevel(Numbering numbering, NumberingProperties properties)
    {
        var numberId = properties.NumberingId!.Val!.Value;
        var levelIndex = properties.NumberingLevelReference!.Val!.Value;
        var instance = numbering.Elements<NumberingInstance>()
            .Single(candidate => candidate.NumberID?.Value == numberId);
        var abstractNumberId = instance.AbstractNumId!.Val!.Value;
        return numbering.Elements<AbstractNum>()
            .Single(candidate => candidate.AbstractNumberId?.Value == abstractNumberId)
            .Elements<Level>()
            .Single(candidate => candidate.LevelIndex?.Value == levelIndex);
    }

    private static string? Style(Paragraph paragraph) => paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
    private static int NumId(Paragraph paragraph) => AssertNumbering(paragraph).NumberingId!.Val!.Value;

    private sealed record ExpectedListParagraph(
        string Text,
        string StyleId,
        NumberFormatValues Format,
        int Level);
}
