using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlInternalLinkValidatorTests
{
    [Fact]
    public void AcceptsGeneratedHyperlinksWithOneBookmarkTarget()
    {
        using var workspace = new SyntheticWorkspace();
        var path = CreateDocument(workspace, includeTarget: true);

        OpenXmlInternalLinkValidator.Validate(path, expectedGeneratedLinkCount: 1);
    }

    [Fact]
    public void RejectsGeneratedHyperlinkWithoutBookmarkTarget()
    {
        using var workspace = new SyntheticWorkspace();
        var path = CreateDocument(workspace, includeTarget: false);

        var exception = Assert.Throws<WorkerCommandException>(
            () => OpenXmlInternalLinkValidator.Validate(path, expectedGeneratedLinkCount: 1));

        Assert.Equal("INTERNAL_LINK_FINALIZATION_FAILED", exception.Code);
        Assert.Equal("openxml-finalize", exception.Stage);
        Assert.Equal(4, exception.ExitCode);
    }

    [Fact]
    public void RejectsWhenWordDropsAnExpectedGeneratedHyperlink()
    {
        using var workspace = new SyntheticWorkspace();
        var path = CreateDocument(workspace, includeTarget: true);

        var exception = Assert.Throws<WorkerCommandException>(
            () => OpenXmlInternalLinkValidator.Validate(path, expectedGeneratedLinkCount: 2));

        Assert.Equal("INTERNAL_LINK_FINALIZATION_FAILED", exception.Code);
        Assert.Equal("openxml-finalize", exception.Stage);
    }

    private static string CreateDocument(SyntheticWorkspace workspace, bool includeTarget)
    {
        var path = workspace.PathFor("internal-link.docx");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var bookmarkName = HtmlConversionService.InternalBookmarkPrefix + "000001";
        var body = new Body();
        if (includeTarget)
        {
            body.Append(new Paragraph(
                new BookmarkStart { Name = bookmarkName, Id = "1" },
                new BookmarkEnd { Id = "1" },
                new Run(new Text("Target"))));
        }
        body.Append(new Paragraph(
            new Hyperlink(new Run(new Text("Jump"))) { Anchor = bookmarkName }));
        mainPart.Document = new Document(body);
        mainPart.Document.Save();
        return path;
    }
}
