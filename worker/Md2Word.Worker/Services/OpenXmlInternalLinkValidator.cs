using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal static class OpenXmlInternalLinkValidator
{
    public static void Validate(string docxPath, int expectedGeneratedLinkCount)
    {
        using var document = WordprocessingDocument.Open(docxPath, false);
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw InvalidInternalLinks("The generated document does not contain a main document body.");
        var bookmarkCounts = body.Descendants<BookmarkStart>()
            .Select(bookmark => bookmark.Name?.Value)
            .Where(IsGeneratedBookmark)
            .Select(name => name!)
            .GroupBy(name => name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var hyperlinkCounts = body.Descendants<Hyperlink>()
            .Select(hyperlink => hyperlink.Anchor?.Value)
            .Where(IsGeneratedBookmark)
            .Select(name => name!)
            .GroupBy(name => name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        if (hyperlinkCounts.Values.Sum() != expectedGeneratedLinkCount)
        {
            throw InvalidInternalLinks(
                $"The final document contains {hyperlinkCounts.Values.Sum()} generated internal hyperlinks; "
                + $"{expectedGeneratedLinkCount} were expected before Word import.");
        }

        var invalidBookmark = bookmarkCounts.FirstOrDefault(entry => entry.Value != 1);
        if (!string.IsNullOrEmpty(invalidBookmark.Key))
        {
            throw InvalidInternalLinks(
                $"Generated internal bookmark '{invalidBookmark.Key}' is not unique in the final document.");
        }

        var missingTarget = hyperlinkCounts.Keys.FirstOrDefault(anchor =>
            !bookmarkCounts.TryGetValue(anchor, out var count) || count != 1);
        if (missingTarget is not null)
        {
            throw InvalidInternalLinks(
                $"Generated internal hyperlink '{missingTarget}' has no unique bookmark target in the final document.");
        }
    }

    private static bool IsGeneratedBookmark(string? value) =>
        value?.StartsWith(HtmlConversionService.InternalBookmarkPrefix, StringComparison.Ordinal) == true;

    private static WorkerCommandException InvalidInternalLinks(string message) => new(
        "INTERNAL_LINK_FINALIZATION_FAILED",
        message,
        4,
        "openxml-finalize");
}
