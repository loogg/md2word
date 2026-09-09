using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;
using V = DocumentFormat.OpenXml.Vml;
using SVG = DocumentFormat.OpenXml.Office2019.Drawing.SVG;

namespace Md2Word.Worker.Tests;

public sealed class GeneratedDocumentOfflineSanitizerTests
{
    private static readonly byte[] SyntheticPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void EmbedsRecognizedLocalDrawingAndVmlImagesAndPreservesHyperlinks()
    {
        using var workspace = new SyntheticWorkspace();
        var imagePath = workspace.WriteBytes("synthetic.png", SyntheticPng);
        var documentPath = workspace.PathFor("linked-images.docx");
        CreateDocumentWithExternalRelationships(documentPath, imagePath);

        var sanitization = new GeneratedDocumentOfflineSanitizer().EmbedLinkedLocalImages(documentPath);
        TemplateValidationService.EnsureGeneratedDocumentIsOfflineSafe(documentPath);

        var recovery = Assert.Single(sanitization.BodyImageRecoveries);
        Assert.StartsWith("MD2WIMG_", recovery.Marker);
        Assert.Equal(Path.GetFullPath(imagePath), recovery.LocalPath);
        Assert.Equal("synthetic alternative text", recovery.OriginalAlternativeText);
        Assert.Equal("synthetic source title", recovery.OriginalTitle);

        using var document = WordprocessingDocument.Open(documentPath, false);
        var mainPart = document.MainDocumentPart!;
        Assert.Empty(mainPart.ExternalRelationships);
        var hyperlink = Assert.Single(mainPart.HyperlinkRelationships);
        Assert.Equal("https://example.invalid/safe-link", hyperlink.Uri.AbsoluteUri);

        var imageParts = mainPart.ImageParts.ToArray();
        Assert.Equal(2, imageParts.Length);
        Assert.All(imageParts, imagePart =>
        {
            using var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            Assert.Equal(SyntheticPng, memory.ToArray());
        });

        var imageRelationshipIds = imageParts
            .Select(mainPart.GetIdOfPart)
            .ToHashSet(StringComparer.Ordinal);
        var blip = Assert.Single(mainPart.Document!.Body!.Descendants<A.Blip>());
        Assert.Null(blip.Link?.Value);
        Assert.NotNull(blip.Embed?.Value);
        Assert.Contains(blip.Embed!.Value!, imageRelationshipIds);
        var vmlImage = Assert.Single(mainPart.Document.Body.Descendants<V.ImageData>());
        Assert.NotNull(vmlImage.RelationshipId?.Value);
        Assert.Contains(vmlImage.RelationshipId!.Value!, imageRelationshipIds);
    }

    [Theory]
    [InlineData("https://example.invalid/remote.png")]
    [InlineData("file://synthetic-server.invalid/share/remote.png")]
    public void LeavesRemoteAndUncImagesBlockedWithoutLeakingTheirTargets(string target)
    {
        using var workspace = new SyntheticWorkspace();
        var documentPath = workspace.PathFor("blocked-image.docx");
        CreateDocumentWithDrawingRelationship(
            documentPath,
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
            new Uri(target));

        new GeneratedDocumentOfflineSanitizer().EmbedLinkedLocalImages(documentPath);

        var exception = Assert.Throws<WorkerCommandException>(
            () => TemplateValidationService.EnsureGeneratedDocumentIsOfflineSafe(documentPath));
        Assert.Equal("OUTPUT_EXTERNAL_CONTENT_BLOCKED", exception.Code);
        Assert.DoesNotContain("example.invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic-server", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmbedsSafeLocalSvgImages()
    {
        using var workspace = new SyntheticWorkspace();
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="20" viewBox="0 0 40 20">
              <defs><marker id="arrow"><path d="M0,0 L4,2 L0,4 z" /></marker></defs>
              <path d="M1,10 L35,10" style="stroke:#000;marker-end:url(#arrow)" />
            </svg>
            """;
        var imagePath = workspace.WriteText("synthetic.svg", svg);
        var documentPath = workspace.PathFor("linked-svg.docx");
        CreateDocumentWithDrawingRelationship(
            documentPath,
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
            new Uri(imagePath),
            includeSvgBlipReference: true);

        var sanitization = new GeneratedDocumentOfflineSanitizer().EmbedLinkedLocalImages(documentPath);
        TemplateValidationService.EnsureGeneratedDocumentIsOfflineSafe(documentPath);

        var recovery = Assert.Single(sanitization.BodyImageRecoveries);
        Assert.StartsWith("MD2WIMG_", recovery.Marker);
        Assert.Equal(Path.GetFullPath(imagePath), recovery.LocalPath);
        Assert.Equal("synthetic alternative text", recovery.OriginalAlternativeText);
        Assert.Equal("synthetic source title", recovery.OriginalTitle);

        using var document = WordprocessingDocument.Open(documentPath, false);
        var imagePart = Assert.Single(document.MainDocumentPart!.ImageParts);
        Assert.Equal("image/svg+xml", imagePart.ContentType);
        var svgBlip = Assert.Single(document.MainDocumentPart.Document!.Body!.Descendants<SVG.SVGBlip>());
        Assert.Null(svgBlip.Link?.Value);
        Assert.Equal(document.MainDocumentPart.GetIdOfPart(imagePart), svgBlip.Embed?.Value);
    }

    [Fact]
    public void EmbedsWordDoubleEncodedUnicodeLocalImageTargets()
    {
        using var workspace = new SyntheticWorkspace();
        var imagePath = workspace.WriteBytes("中文图像.png", SyntheticPng);
        var wordEscapedTarget = new Uri(new Uri(imagePath).AbsoluteUri.Replace("%", "%25", StringComparison.Ordinal));
        var documentPath = workspace.PathFor("double-encoded-unicode-image.docx");
        CreateDocumentWithDrawingRelationship(
            documentPath,
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
            wordEscapedTarget);

        var sanitization = new GeneratedDocumentOfflineSanitizer().EmbedLinkedLocalImages(documentPath);
        TemplateValidationService.EnsureGeneratedDocumentIsOfflineSafe(documentPath);

        var recovery = Assert.Single(sanitization.BodyImageRecoveries);
        Assert.StartsWith("MD2WIMG_", recovery.Marker);
        Assert.Equal(Path.GetFullPath(imagePath), recovery.LocalPath);
        Assert.Equal("synthetic alternative text", recovery.OriginalAlternativeText);
        Assert.Equal("synthetic source title", recovery.OriginalTitle);

        using var document = WordprocessingDocument.Open(documentPath, false);
        var mainPart = document.MainDocumentPart!;
        Assert.Empty(mainPart.ExternalRelationships);
        var imagePart = Assert.Single(mainPart.ImageParts);
        using var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(SyntheticPng, memory.ToArray());
        var properties = Assert.Single(mainPart.Document!.Body!.Descendants<DW.DocProperties>());
        Assert.Equal("synthetic alternative text", properties.Description?.Value);
        Assert.Equal("synthetic source title", properties.Title?.Value);
        var bookmarkStart = Assert.Single(
            mainPart.Document.Body.Descendants<BookmarkStart>(),
            bookmark => string.Equals(bookmark.Name?.Value, recovery.Marker, StringComparison.Ordinal));
        var bookmarkEnd = Assert.Single(
            mainPart.Document.Body.Descendants<BookmarkEnd>(),
            bookmark => string.Equals(bookmark.Id?.Value, bookmarkStart.Id?.Value, StringComparison.Ordinal));
        Assert.NotNull(bookmarkEnd);
    }

    [Fact]
    public void RestoresImageMetadataAndRemovesRecoveryBookmarks()
    {
        using var workspace = new SyntheticWorkspace();
        var imagePath = workspace.WriteBytes("合成图像.png", SyntheticPng);
        var documentPath = workspace.PathFor("recovered-image-metadata.docx");
        CreateDocumentWithDrawingRelationship(
            documentPath,
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
            new Uri(imagePath));
        var sanitizer = new GeneratedDocumentOfflineSanitizer();
        var sanitization = sanitizer.EmbedLinkedLocalImages(documentPath);

        sanitizer.RestoreRecoveredImageMetadata(
            documentPath,
            sanitization.BodyImageRecoveries);

        using var document = WordprocessingDocument.Open(documentPath, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        Assert.DoesNotContain(
            body.Descendants<BookmarkStart>(),
            bookmark => bookmark.Name?.Value?.StartsWith("MD2WIMG_", StringComparison.Ordinal) == true);
        var properties = Assert.Single(body.Descendants<DW.DocProperties>());
        Assert.Equal("synthetic alternative text", properties.Description?.Value);
        Assert.Equal("synthetic source title", properties.Title?.Value);
    }

    [Fact]
    public void LeavesActiveLocalSvgBlocked()
    {
        using var workspace = new SyntheticWorkspace();
        var imagePath = workspace.WriteText(
            "active.svg",
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var documentPath = workspace.PathFor("active-svg.docx");
        CreateDocumentWithDrawingRelationship(
            documentPath,
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
            new Uri(imagePath));

        new GeneratedDocumentOfflineSanitizer().EmbedLinkedLocalImages(documentPath);

        var exception = Assert.Throws<WorkerCommandException>(
            () => TemplateValidationService.EnsureGeneratedDocumentIsOfflineSafe(documentPath));
        Assert.Equal("OUTPUT_EXTERNAL_CONTENT_BLOCKED", exception.Code);
    }

    [Fact]
    public void LeavesUnknownExternalRelationshipTypesBlocked()
    {
        using var workspace = new SyntheticWorkspace();
        var localImage = workspace.WriteBytes("synthetic.png", SyntheticPng);
        var documentPath = workspace.PathFor("unknown-relationship.docx");
        CreateDocumentWithDrawingRelationship(
            documentPath,
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject",
            new Uri(localImage));

        new GeneratedDocumentOfflineSanitizer().EmbedLinkedLocalImages(documentPath);

        var exception = Assert.Throws<WorkerCommandException>(
            () => TemplateValidationService.EnsureGeneratedDocumentIsOfflineSafe(documentPath));
        Assert.Equal("OUTPUT_EXTERNAL_CONTENT_BLOCKED", exception.Code);
        Assert.DoesNotContain(localImage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeavesUnrecognizedLocalImageReferencesBlocked()
    {
        using var workspace = new SyntheticWorkspace();
        var localImage = workspace.WriteBytes("synthetic.png", SyntheticPng);
        var documentPath = workspace.PathFor("unrecognized-image.docx");
        using (var document = WordprocessingDocument.Create(documentPath, WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("synthetic")))));
            mainPart.AddExternalRelationship(
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
                new Uri(localImage),
                "rIdUnusedExternalImage");
            mainPart.Document.Save();
        }

        new GeneratedDocumentOfflineSanitizer().EmbedLinkedLocalImages(documentPath);

        var exception = Assert.Throws<WorkerCommandException>(
            () => TemplateValidationService.EnsureGeneratedDocumentIsOfflineSafe(documentPath));
        Assert.Equal("OUTPUT_EXTERNAL_CONTENT_BLOCKED", exception.Code);
    }

    [Fact]
    public void TemplateValidationStillRejectsLocalExternalImages()
    {
        using var workspace = new SyntheticWorkspace();
        var localImage = workspace.WriteBytes("synthetic.png", SyntheticPng);
        var template = workspace.CreateTemplate([
            ("Body", "正文", null),
            ("Code", "CodeBlock", null),
        ]);
        using (var document = WordprocessingDocument.Open(template, true))
        {
            document.MainDocumentPart!.AddExternalRelationship(
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
                new Uri(localImage),
                "rIdLocalTemplateImage");
        }
        var css = workspace.WriteText("style.css", """
            p.manual-body-paragraph { mso-style-name: "正文"; }
            li.manual-body-item { mso-style-name: "正文"; }
            p.manual-code-block-paragraph { mso-style-name: "CodeBlock"; }
            """);

        var report = new TemplateValidationService().Validate(template, css).Report;

        Assert.Equal("invalid", report.Status);
        Assert.Contains(report.Issues, issue => issue.Code == "TEMPLATE_EXTERNAL_CONTENT_BLOCKED");
    }

    private static void CreateDocumentWithExternalRelationships(string documentPath, string imagePath)
    {
        using var document = WordprocessingDocument.Create(documentPath, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        const string drawingRelationshipId = "rIdDrawingExternal";
        const string vmlRelationshipId = "rIdVmlExternal";
        const string hyperlinkRelationshipId = "rIdHyperlink";
        var imageUri = new Uri(imagePath);
        mainPart.AddExternalRelationship(
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
            imageUri,
            drawingRelationshipId);
        mainPart.AddExternalRelationship(
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
            imageUri,
            vmlRelationshipId);
        mainPart.AddHyperlinkRelationship(
            new Uri("https://example.invalid/safe-link"),
            isExternal: true,
            hyperlinkRelationshipId);

        var drawing = new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = 306705L, Cy = 306705L },
                new DW.DocProperties
                {
                    Id = 1U,
                    Name = "Synthetic picture",
                    Description = "synthetic alternative text",
                    Title = "synthetic source title",
                },
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.BlipFill(new A.Blip { Link = drawingRelationshipId })))
                    {
                        Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture",
                    })));
        var vmlPicture = new Picture(
            new V.Shape(
                new V.ImageData { RelationshipId = vmlRelationshipId }));
        var hyperlink = new Hyperlink(new Run(new Text("safe hyperlink")))
        {
            Id = hyperlinkRelationshipId,
        };
        mainPart.Document = new Document(new Body(
            new Paragraph(new Run(drawing)),
            new Paragraph(new Run(vmlPicture)),
            new Paragraph(hyperlink)));
        mainPart.Document.Save();
    }

    private static void CreateDocumentWithDrawingRelationship(
        string documentPath,
        string relationshipType,
        Uri target,
        bool includeSvgBlipReference = false)
    {
        using var document = WordprocessingDocument.Create(documentPath, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        const string relationshipId = "rIdExternal";
        mainPart.AddExternalRelationship(relationshipType, target, relationshipId);
        var drawingBlip = new A.Blip { Link = relationshipId };
        if (includeSvgBlipReference)
        {
            drawingBlip.Append(new A.BlipExtensionList(
                new A.BlipExtension(new SVG.SVGBlip { Link = relationshipId })
                {
                    Uri = "{28A0092B-C50C-407E-A947-70E740481C1C}",
                }));
        }
        var drawing = new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = 306705L, Cy = 306705L },
                new DW.DocProperties
                {
                    Id = 1U,
                    Name = "Synthetic picture",
                    Description = "synthetic alternative text",
                    Title = "synthetic source title",
                },
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.BlipFill(drawingBlip)))
                    {
                        Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture",
                    })));
        mainPart.Document = new Document(new Body(new Paragraph(new Run(drawing))));
        mainPart.Document.Save();
    }
}
