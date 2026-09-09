using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlResolvedStyleRebinderTests
{
    [Fact]
    public void RebindsStylesThatWordRenumberedOrLocalizedAfterSaving()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("word-renumbered-styles.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();
            var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylePart.Styles = new Styles(
                ParagraphStyle("a", "Normal", isDefault: true),
                ParagraphStyle("af9", "图注"),
                ParagraphStyle("CodeBlock", "CodeBlock"));
            stylePart.Styles.Save();
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("content")))));
            mainPart.Document.Save();
        }
        var preWordStyles = new Dictionary<string, ResolvedStyle>
        {
            [StyleRoles.Body] = new("MD2WordBody", "正文"),
            [StyleRoles.OrderedList] = new("MD2WordBody", "正文"),
            [StyleRoles.Caption] = new("MD2WordCaption", "图注"),
            [StyleRoles.CodeBlock] = new("CodeBlock", "CodeBlock"),
        };

        var rebound = OpenXmlResolvedStyleRebinder.Rebind(path, preWordStyles);

        Assert.Equal("a", rebound[StyleRoles.Body].StyleId);
        Assert.Equal("a", rebound[StyleRoles.OrderedList].StyleId);
        Assert.Equal("af9", rebound[StyleRoles.Caption].StyleId);
        Assert.Equal("CodeBlock", rebound[StyleRoles.CodeBlock].StyleId);
    }

    [Fact]
    public void FailsClosedWhenWordRemovedAnUnrecoverableMappedStyle()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("missing-word-style.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();
            var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylePart.Styles = new Styles(ParagraphStyle("Normal", "Normal", isDefault: true));
            stylePart.Styles.Save();
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("content")))));
            mainPart.Document.Save();
        }

        var exception = Assert.Throws<WorkerCommandException>(() =>
            OpenXmlResolvedStyleRebinder.Rebind(
                path,
                new Dictionary<string, ResolvedStyle>
                {
                    [StyleRoles.Caption] = new("RemovedCaption", "Private Caption"),
                }));

        Assert.Equal("WORD_STYLE_REBIND_FAILED", exception.Code);
        Assert.Equal("openxml-finalize", exception.Stage);
    }

    private static Style ParagraphStyle(string id, string name, bool isDefault = false) =>
        new(new StyleName { Val = name })
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
            Default = isDefault,
        };
}
