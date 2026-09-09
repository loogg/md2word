using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlFinalizationPipelineTests
{
    private static readonly IReadOnlyDictionary<string, ResolvedStyle> RoleStyles =
        new Dictionary<string, ResolvedStyle>
        {
            [StyleRoles.OrderedList] = new("ExampleOrdered", "示例 有序列项"),
            [StyleRoles.UnorderedList] = new("ExampleBody", "示例 正文"),
        };

    [Fact]
    public void LooseOrderedContinuationPreservesNumberingContextInProductionOrder()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.CreateListDocument(
            includeContinuation: true,
            includeRoleMarkers: true);

        var result = new OpenXmlFinalizationPipeline().Finalize(path, RoleStyles, bodyOnly: true);

        Assert.Equal(5, result.Lists.NormalizedParagraphs);
        Assert.Equal(2, result.Lists.RestartedOrderedLists);
        Assert.Equal(4, result.Roles.MarkersRemoved);
        Assert.Equal(4, result.Roles.StylesApplied);

        using var document = WordprocessingDocument.Open(path, false);
        var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Where(paragraph => paragraph.InnerText.Length > 0)
            .ToDictionary(paragraph => paragraph.InnerText, StringComparer.Ordinal);
        var first = paragraphs["ordered-a"];
        var continuation = paragraphs["ordered continuation"];
        var second = paragraphs["ordered-b"];
        var separate = paragraphs["ordered-new-block"];

        Assert.Equal("ExampleOrdered", Style(first));
        Assert.Equal("ExampleOrdered", Style(continuation));
        Assert.Equal("ExampleOrdered", Style(second));
        Assert.Equal("ExampleOrdered", Style(separate));
        Assert.NotNull(NumId(first));
        Assert.Equal(NumId(first), NumId(second));
        Assert.NotEqual(NumId(first), NumId(separate));
        Assert.Null(continuation.ParagraphProperties!.NumberingProperties);
        Assert.Null(continuation.ParagraphProperties.Indentation);
        Assert.DoesNotContain("__MD2WORD_ROLE_", continuation.InnerText, StringComparison.Ordinal);
    }

    private static string? Style(Paragraph paragraph) => paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
    private static int? NumId(Paragraph paragraph) => paragraph.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value;
}
