using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlHeadingNumberingNormalizerTests
{
    [Fact]
    public void ReusesOneNumberingInstanceAcrossBodyParagraphsBetweenHeadings()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("heading-numbering.docx");
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = package.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                Heading("__MD2WORD_ROLE_11111111111111111111111111111111_h1__First", 21, 0),
                new Paragraph(new Run(new Text("Body paragraph"))),
                Heading("__MD2WORD_ROLE_22222222222222222222222222222222_h2__Second", 22, 1),
                new Paragraph(new Run(new Text("Another body paragraph"))),
                Heading("__MD2WORD_ROLE_33333333333333333333333333333333_h1__Third", 23, 0)));
            mainPart.Document.Save();
        }

        var result = OpenXmlHeadingNumberingNormalizer.Normalize(path);

        Assert.Equal(3, result.NumberedHeadings);
        Assert.Equal(2, result.ReassignedHeadings);
        using var document = WordprocessingDocument.Open(path, false);
        var ids = document.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>()
            .Where(paragraph => paragraph.InnerText.Contains("__MD2WORD_ROLE_", StringComparison.Ordinal))
            .Select(paragraph => paragraph.ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value)
            .Distinct()
            .ToArray();
        Assert.Equal([21], ids);
    }

    private static Paragraph Heading(string text, int numberId, int level) => new(
        new ParagraphProperties(new NumberingProperties(
            new NumberingLevelReference { Val = level },
            new NumberingId { Val = numberId })),
        new Run(new Text(text)));
}
