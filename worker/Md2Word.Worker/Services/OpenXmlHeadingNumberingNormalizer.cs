using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Md2Word.Worker.Services;

internal sealed record HeadingNumberingNormalizationResult(int NumberedHeadings, int ReassignedHeadings);

internal static partial class OpenXmlHeadingNumberingNormalizer
{
    [GeneratedRegex(@"__MD2WORD_ROLE_[0-9a-fA-F]{32}_h[1-4]__", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingMarkerRegex();

    public static HeadingNumberingNormalizationResult Normalize(string docxPath)
    {
        using var document = WordprocessingDocument.Open(docxPath, true);
        var wordDocument = document.MainDocumentPart?.Document
            ?? throw new InvalidDataException("The generated DOCX has no main document.");
        var numberedHeadings = wordDocument.Body!
            .Descendants<Paragraph>()
            .Where(paragraph => HeadingMarkerRegex().IsMatch(paragraph.InnerText))
            .Select(paragraph => paragraph.ParagraphProperties?.NumberingProperties)
            .Where(properties => properties?.NumberingId?.Val?.Value is > 0)
            .Cast<NumberingProperties>()
            .ToArray();
        if (numberedHeadings.Length == 0)
        {
            return new HeadingNumberingNormalizationResult(0, 0);
        }

        var canonicalNumberId = numberedHeadings[0].NumberingId!.Val!.Value;
        var reassigned = 0;
        foreach (var properties in numberedHeadings.Skip(1))
        {
            if (properties.NumberingId!.Val!.Value == canonicalNumberId)
            {
                continue;
            }
            properties.NumberingId.Val = canonicalNumberId;
            reassigned++;
        }
        if (reassigned > 0)
        {
            wordDocument.Save();
        }
        return new HeadingNumberingNormalizationResult(numberedHeadings.Length, reassigned);
    }
}
