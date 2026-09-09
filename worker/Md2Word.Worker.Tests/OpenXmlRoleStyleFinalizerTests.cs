using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace Md2Word.Worker.Tests;

public sealed class OpenXmlRoleStyleFinalizerTests
{
    private const string OrderedMarker = "__MD2WORD_ROLE_0123456789abcdef0123456789abcdef_ordered-list__";
    private const string CodeMarker = "__MD2WORD_ROLE_fedcba9876543210fedcba9876543210_code-block__";
    private const string HeadingMarker = "__MD2WORD_ROLE_22222222222222222222222222222222_h1__";
    private const string BoundaryMarker = "__MD2WORD_ROLE_11111111111111111111111111111111_import-boundary__";

    private static readonly IReadOnlyDictionary<string, ResolvedStyle> RoleStyles =
        new Dictionary<string, ResolvedStyle>
        {
            [StyleRoles.OrderedList] = new("ExampleOrdered", "示例 有序列项"),
            [StyleRoles.CodeBlock] = new("ExampleCode", "示例 代码"),
            [StyleRoles.Heading1] = new("ExampleHeading1", "示例 标题1"),
        };

    [Fact]
    public void RemovesMarkersAppliesRoleStylesAndPreservesNativeNumbering()
    {
        using var workspace = new SyntheticWorkspace();
        var path = CreateRoleMarkerDocument(workspace);

        var result = OpenXmlRoleStyleFinalizer.Finalize(path, RoleStyles, bodyOnly: true);

        Assert.Equal(4, result.MarkersRemoved);
        Assert.Equal(2, result.StylesApplied);

        using var document = WordprocessingDocument.Open(path, false);
        var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToArray();
        Assert.DoesNotContain(paragraphs, paragraph => paragraph.InnerText.Contains("__MD2WORD_ROLE_", StringComparison.Ordinal));
        Assert.DoesNotContain(paragraphs, paragraph => paragraph.InnerText == "Import boundary");

        var cachedTocEntry = paragraphs.Single(paragraph => paragraph.InnerText == "Cached TOC entry");
        Assert.Equal("TOC1", cachedTocEntry.ParagraphProperties!.ParagraphStyleId!.Val!.Value);

        var ordered = paragraphs.Single(paragraph => paragraph.InnerText == "Ordered item");
        Assert.Equal("ExampleOrdered", ordered.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.Equal(2, ordered.ParagraphProperties.NumberingProperties!.NumberingLevelReference!.Val!.Value);
        Assert.Equal(41, ordered.ParagraphProperties.NumberingProperties.NumberingId!.Val!.Value);
        Assert.Equal("240", ordered.ParagraphProperties.SpacingBetweenLines?.Line?.Value);
        Assert.Equal(
            LineSpacingRuleValues.Auto,
            ordered.ParagraphProperties.SpacingBetweenLines?.LineRule?.Value);

        var code = paragraphs.Single(paragraph => paragraph.InnerText == "Code sample");
        Assert.Equal("ExampleCode", code.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.Null(code.ParagraphProperties.Indentation);
    }

    [Fact]
    public void PreservesBodyEndBookmarkWhenWordPlacesItInBoundaryParagraph()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("boundary-bookmark.docx");
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = package.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                BookmarkParagraph(TemplateValidationService.BodyStartBookmark, "1"),
                new Paragraph(new Run(new Text(OrderedMarker + "Body"))),
                new Paragraph(
                    new BookmarkStart { Name = TemplateValidationService.BodyEndBookmark, Id = "2" },
                    new Run(new Text(BoundaryMarker)),
                    new BookmarkEnd { Id = "2" })));
            mainPart.Document.Save();
        }

        OpenXmlRoleStyleFinalizer.Finalize(path, RoleStyles, bodyOnly: true);

        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        Assert.Contains(
            body.Descendants<BookmarkStart>(),
            bookmark => bookmark.Name?.Value == TemplateValidationService.BodyEndBookmark);
        Assert.DoesNotContain(
            body.Descendants<Paragraph>(),
            paragraph => paragraph.InnerText.Contains("__MD2WORD_ROLE_", StringComparison.Ordinal));
    }

    [Fact]
    public void RemovesTableRoleMarkersSplitByRenderedPageBreaks()
    {
        const string tableMarker = "__MD2WORD_ROLE_77777777777777777777777777777777_table__";
        var roleStyles = new Dictionary<string, ResolvedStyle>
        {
            [StyleRoles.Table] = new("MD2WordBody", "Body"),
        };
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("split-table-role-markers.docx");
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = package.AddMainDocumentPart();
            var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylePart.Styles = new Styles(
                new Style(new StyleName { Val = "Body" })
                {
                    Type = StyleValues.Paragraph,
                    StyleId = "MD2WordBody",
                },
                new Style(new StyleName { Val = "md2word-role-marker" })
                {
                    Type = StyleValues.Character,
                    StyleId = "md2word-role-marker",
                    CustomStyle = true,
                });
            stylePart.Styles.Save();
            mainPart.Document = new Document(new Body(
                BookmarkParagraph(TemplateValidationService.BodyStartBookmark, "1"),
                new Table(
                    new TableRow(
                        SplitMarkerCell(tableMarker, "MID 0715"),
                        SplitMarkerCell(tableMarker, "示教器参数查询、参数设置、工具控制、订阅管理等"),
                        SplitMarkerCell(tableMarker, "客户端"))),
                BookmarkParagraph(TemplateValidationService.BodyEndBookmark, "2")));
            mainPart.Document.Save();
        }

        var result = OpenXmlRoleStyleFinalizer.Finalize(path, roleStyles, bodyOnly: true);

        Assert.Equal(3, result.MarkersRemoved);
        Assert.Equal(3, result.StylesApplied);
        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        var cellParagraphs = Assert.Single(body.Elements<Table>())
            .Descendants<TableCell>()
            .SelectMany(cell => cell.Elements<Paragraph>())
            .ToArray();
        Assert.Equal(new[]
        {
            "MID 0715",
            "示教器参数查询、参数设置、工具控制、订阅管理等",
            "客户端",
        }, cellParagraphs.Select(paragraph => paragraph.InnerText));
        Assert.All(
            cellParagraphs,
            paragraph => Assert.Equal(
                "MD2WordBody",
                paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value));
        Assert.Equal(3, cellParagraphs.SelectMany(paragraph => paragraph.Descendants<LastRenderedPageBreak>()).Count());
        Assert.DoesNotContain(
            body.Descendants<RunStyle>(),
            style => style.Val?.Value == "md2word-role-marker");
        Assert.DoesNotContain(
            document.MainDocumentPart.StyleDefinitionsPart!.Styles!.Elements<Style>(),
            style => style.StyleId?.Value == "md2word-role-marker");
        Assert.DoesNotContain(
            body.Descendants<Paragraph>(),
            paragraph => paragraph.InnerText.Contains("__MD2WORD_ROLE_", StringComparison.Ordinal));
    }

    [Fact]
    public void FailsClosedWhenAnOrphanRoleMarkerStyleReferenceRemains()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("orphan-role-marker-style.docx");
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = package.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                BookmarkParagraph(TemplateValidationService.BodyStartBookmark, "1"),
                new Paragraph(
                    new Run(
                        new RunProperties(new RunStyle { Val = "md2word-role-marker" }),
                        new Text("orphan marker-formatted content"))),
                BookmarkParagraph(TemplateValidationService.BodyEndBookmark, "2")));
            mainPart.Document.Save();
        }

        var exception = Assert.Throws<WorkerCommandException>(() =>
            OpenXmlRoleStyleFinalizer.Finalize(path, RoleStyles, bodyOnly: true));

        Assert.Equal("ROLE_MARKER_FINALIZATION_FAILED", exception.Code);
        Assert.Equal("openxml-finalize", exception.Stage);
    }

    [Fact]
    public void HeadingStyleRemovesImportedDirectFontFormattingButKeepsCharacterStyle()
    {
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("heading-formatting.docx");
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = package.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                BookmarkParagraph(TemplateValidationService.BodyStartBookmark, "1"),
                new Paragraph(
                    new ParagraphProperties(
                        new ParagraphStyleId { Val = "ImportedHeading" },
                        new SpacingBetweenLines { Before = "240" }),
                    new Run(
                        new RunProperties(
                            new RunStyle { Val = "Emphasis" },
                            new RunFonts { Ascii = "Arial", EastAsia = "Arial" },
                            new Bold(),
                            new FontSize { Val = "48" },
                            new Kern { Val = 36 }),
                        new Text(HeadingMarker + "Heading"))),
                BookmarkParagraph(TemplateValidationService.BodyEndBookmark, "2")));
            mainPart.Document.Save();
        }

        OpenXmlRoleStyleFinalizer.Finalize(path, RoleStyles, bodyOnly: true);

        using var document = WordprocessingDocument.Open(path, false);
        var heading = document.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>()
            .Single(paragraph => paragraph.InnerText == "Heading");
        Assert.Equal("ExampleHeading1", heading.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        var runProperties = Assert.Single(heading.Descendants<RunProperties>());
        Assert.Equal("Emphasis", runProperties.RunStyle?.Val?.Value);
        Assert.Null(runProperties.RunFonts);
        Assert.Null(runProperties.Bold);
        Assert.Null(runProperties.FontSize);
        Assert.Null(runProperties.Kern);
    }

    [Fact]
    public void AppliesExtendedNativeFallbackRolesAndKeepsImageLayoutOverrides()
    {
        const string figureMarker = "__MD2WORD_ROLE_33333333333333333333333333333333_figure-image__";
        const string headingFiveMarker = "__MD2WORD_ROLE_44444444444444444444444444444444_h5__";
        const string admonitionMarker = "__MD2WORD_ROLE_55555555555555555555555555555555_admonition__";
        const string tableMarker = "__MD2WORD_ROLE_66666666666666666666666666666666_table__";
        var roleStyles = new Dictionary<string, ResolvedStyle>
        {
            [StyleRoles.FigureImage] = new("Normal", "Normal"),
            [StyleRoles.Heading5] = new("Heading5", "heading 5"),
            [StyleRoles.Admonition] = new("IntenseQuote", "Intense Quote"),
            [StyleRoles.Table] = new("Normal", "Normal"),
        };

        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("extended-role-markers.docx");
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = package.AddMainDocumentPart();
            var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylePart.Styles = new Styles(
                new Style(new StyleName { Val = "Strong" })
                {
                    Type = StyleValues.Character,
                    StyleId = "Strong",
                },
                new Style(new StyleName { Val = "admonition-label" })
                {
                    Type = StyleValues.Character,
                    StyleId = "admonition-label",
                    CustomStyle = true,
                },
                new Style(new StyleName { Val = "md2word-role-marker" })
                {
                    Type = StyleValues.Character,
                    StyleId = "md2word-role-marker",
                    CustomStyle = true,
                },
                new Style(new StyleName { Val = "manual-table-paragraph" })
                {
                    Type = StyleValues.Paragraph,
                    StyleId = "manual-table-paragraph",
                    CustomStyle = true,
                });
            stylePart.Styles.Save();
            mainPart.Document = new Document(new Body(
                BookmarkParagraph(TemplateValidationService.BodyStartBookmark, "1"),
                new Paragraph(
                    new ParagraphProperties(
                        new ParagraphStyleId { Val = "Imported" },
                        new SpacingBetweenLines { Before = "120" },
                        new Justification { Val = JustificationValues.Center }),
                    new Run(new Text(figureMarker + "Figure image"))),
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "ImportedHeading" }),
                    new Run(
                        new RunProperties(
                            new RunStyle { Val = "Emphasis" },
                            new RunFonts { Ascii = "Arial" },
                            new Bold(),
                            new FontSize { Val = "32" }),
                        new Text(headingFiveMarker + "Heading five"))),
                new Paragraph(
                    new ParagraphProperties(new Indentation { Left = "720" }),
                    new Run(
                        new RunProperties(
                            new RunStyle { Val = "admonition-label" },
                            new RunFonts { Hint = FontTypeHintValues.EastAsia }),
                        new Text(admonitionMarker + "Notice"))),
                new Paragraph(
                    new ParagraphProperties(new SpacingBetweenLines { After = "160" }),
                    new Run(
                        new RunProperties(new RunFonts { Hint = FontTypeHintValues.EastAsia }),
                        new Text(tableMarker + "Table text"))),
                BookmarkParagraph(TemplateValidationService.BodyEndBookmark, "2")));
            mainPart.Document.Save();
        }

        var result = OpenXmlRoleStyleFinalizer.Finalize(path, roleStyles, bodyOnly: true);

        Assert.Equal(4, result.StylesApplied);
        using var document = WordprocessingDocument.Open(path, false);
        var paragraphs = document.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().ToArray();
        var image = paragraphs.Single(paragraph => paragraph.InnerText == "Figure image");
        Assert.Equal("Normal", image.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        Assert.Equal(JustificationValues.Center, image.ParagraphProperties?.Justification?.Val?.Value);
        Assert.Equal("240", image.ParagraphProperties?.SpacingBetweenLines?.Line?.Value);
        Assert.Equal(
            LineSpacingRuleValues.Auto,
            image.ParagraphProperties?.SpacingBetweenLines?.LineRule?.Value);

        var heading = paragraphs.Single(paragraph => paragraph.InnerText == "Heading five");
        Assert.Equal("Heading5", heading.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        var headingRunProperties = Assert.Single(heading.Descendants<RunProperties>());
        Assert.Equal("Emphasis", headingRunProperties.RunStyle?.Val?.Value);
        Assert.Null(headingRunProperties.RunFonts);
        Assert.Null(headingRunProperties.Bold);
        Assert.Null(headingRunProperties.FontSize);

        var admonition = paragraphs.Single(paragraph => paragraph.InnerText == "Notice");
        Assert.Equal("IntenseQuote", admonition.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        Assert.Null(admonition.ParagraphProperties?.Indentation);
        var admonitionRunProperties = Assert.Single(admonition.Descendants<RunProperties>());
        Assert.Equal("Strong", admonitionRunProperties.RunStyle?.Val?.Value);
        Assert.Null(admonitionRunProperties.RunFonts);

        var table = paragraphs.Single(paragraph => paragraph.InnerText == "Table text");
        Assert.Equal("Normal", table.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        Assert.Null(table.ParagraphProperties?.SpacingBetweenLines);
        Assert.Empty(table.Descendants<RunProperties>());

        var remainingStyleIds = document.MainDocumentPart!.StyleDefinitionsPart!.Styles!
            .Elements<Style>()
            .Select(style => style.StyleId?.Value)
            .ToArray();
        Assert.DoesNotContain("admonition-label", remainingStyleIds);
        Assert.DoesNotContain("md2word-role-marker", remainingStyleIds);
        Assert.DoesNotContain("manual-table-paragraph", remainingStyleIds);
    }

    [Fact]
    public void AppliesFixedAdmonitionPaletteWithoutChangingMappedStyleGeometry()
    {
        var roleStyles = new Dictionary<string, ResolvedStyle>
        {
            [StyleRoles.Admonition] = new("MappedAdmonition", "Mapped admonition"),
        };
        var expected = new[]
        {
            ("Generic quote", HtmlConversionService.AdmonitionVisualGenericRole, "F8FAFC", "64748B"),
            ("First note paragraph", HtmlConversionService.AdmonitionVisualNoteRole, "EFF6FF", "2563EB"),
            ("Second note paragraph", HtmlConversionService.AdmonitionVisualNoteRole, "EFF6FF", "2563EB"),
            ("Caution text", HtmlConversionService.AdmonitionVisualCautionRole, "FFFBEB", "B45309"),
            ("Warning text", HtmlConversionService.AdmonitionVisualWarningRole, "FEF2F2", "B91C1C"),
            ("Danger text", HtmlConversionService.AdmonitionVisualDangerRole, "FEE2E2", "7F1D1D"),
        };
        using var workspace = new SyntheticWorkspace();
        var path = workspace.PathFor("admonition-palette.docx");
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = package.AddMainDocumentPart();
            var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylePart.Styles = new Styles(
                new Style(
                    new StyleName { Val = "Mapped admonition" },
                    new StyleParagraphProperties(
                        new SpacingBetweenLines { Before = "120", After = "160", Line = "276" },
                        new Indentation { Left = "360", FirstLine = "240" },
                        new Justification { Val = JustificationValues.Both }),
                    new StyleRunProperties(
                        new RunFonts { Ascii = "Calibri", EastAsia = "宋体" },
                        new FontSize { Val = "22" }))
                {
                    Type = StyleValues.Paragraph,
                    StyleId = "MappedAdmonition",
                },
                new Style(new StyleName { Val = "Fixed template paragraph" })
                {
                    Type = StyleValues.Paragraph,
                    StyleId = "FixedTemplate",
                },
                new Style(new StyleName { Val = "md2word-role-marker" })
                {
                    Type = StyleValues.Character,
                    StyleId = "md2word-role-marker",
                    CustomStyle = true,
                });
            stylePart.Styles.Save();
            var bodyChildren = new List<OpenXmlElement>
            {
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "FixedTemplate" }),
                    new Run(new Text(
                        RoleMarker('a', StyleRoles.Admonition)
                        + RoleMarker('b', HtmlConversionService.AdmonitionVisualWarningRole)
                        + "Fixed template quote"))),
                BookmarkParagraph(TemplateValidationService.BodyStartBookmark, "1"),
            };
            bodyChildren.AddRange(expected.Select(item => AdmonitionParagraph(item.Item1, item.Item2)));
            bodyChildren.Add(BookmarkParagraph(TemplateValidationService.BodyEndBookmark, "2"));
            mainPart.Document = new Document(new Body(bodyChildren));
            mainPart.Document.Save();
        }

        OpenXmlRoleStyleFinalizer.Finalize(path, roleStyles, bodyOnly: true);

        using var document = WordprocessingDocument.Open(path, false);
        var mainDocumentPart = document.MainDocumentPart!;
        var paragraphs = mainDocumentPart.Document!.Body!.Descendants<Paragraph>().ToArray();
        var fixedTemplateParagraph = paragraphs.Single(paragraph => paragraph.InnerText == "Fixed template quote");
        Assert.Equal(
            "FixedTemplate",
            fixedTemplateParagraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        Assert.Null(fixedTemplateParagraph.ParagraphProperties?.ParagraphBorders);
        Assert.Null(fixedTemplateParagraph.ParagraphProperties?.Shading);

        foreach (var (text, _, fillColor, borderColor) in expected)
        {
            var paragraph = paragraphs.Single(candidate => candidate.InnerText == text);
            var properties = Assert.IsType<ParagraphProperties>(paragraph.ParagraphProperties);
            Assert.Equal("MappedAdmonition", properties.ParagraphStyleId?.Val?.Value);
            Assert.Null(properties.Indentation);
            Assert.Null(properties.SpacingBetweenLines);
            Assert.Null(properties.Justification);
            Assert.Null(properties.NumberingProperties);
            Assert.Empty(paragraph.Descendants<RunProperties>());

            var borders = Assert.IsType<ParagraphBorders>(properties.ParagraphBorders);
            var left = Assert.IsType<LeftBorder>(borders.LeftBorder);
            Assert.Equal(BorderValues.Single, left.Val?.Value);
            Assert.Equal(borderColor, left.Color?.Value);
            Assert.Equal(18U, left.Size?.Value);
            Assert.Equal(0U, left.Space?.Value);
            Assert.Null(borders.TopBorder);
            Assert.Null(borders.RightBorder);
            Assert.Null(borders.BottomBorder);

            var shading = Assert.IsType<Shading>(properties.Shading);
            Assert.Equal(ShadingPatternValues.Clear, shading.Val?.Value);
            Assert.Equal("auto", shading.Color?.Value);
            Assert.Equal(fillColor, shading.Fill?.Value);
        }

        var mappedStyle = mainDocumentPart.StyleDefinitionsPart!.Styles!
            .Elements<Style>()
            .Single(style => style.StyleId?.Value == "MappedAdmonition");
        var mappedProperties = Assert.IsType<StyleParagraphProperties>(mappedStyle.StyleParagraphProperties);
        Assert.Equal("120", mappedProperties.SpacingBetweenLines?.Before?.Value);
        Assert.Equal("160", mappedProperties.SpacingBetweenLines?.After?.Value);
        Assert.Equal("276", mappedProperties.SpacingBetweenLines?.Line?.Value);
        Assert.Equal("360", mappedProperties.Indentation?.Left?.Value);
        Assert.Equal("240", mappedProperties.Indentation?.FirstLine?.Value);
        Assert.Equal(JustificationValues.Both, mappedProperties.Justification?.Val?.Value);
        Assert.DoesNotContain(
            paragraphs,
            paragraph => paragraph.InnerText.Contains("__MD2WORD_ROLE_", StringComparison.Ordinal));
        Assert.DoesNotContain(
            mainDocumentPart.StyleDefinitionsPart.Styles.Elements<Style>(),
            style => style.StyleId?.Value == "md2word-role-marker");
    }

    private static string CreateRoleMarkerDocument(SyntheticWorkspace workspace)
    {
        var path = workspace.PathFor("role-markers.docx");
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = package.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(
            new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "TOC1" }),
                new Run(new Text(OrderedMarker + "Cached TOC entry"))),
            BookmarkParagraph(TemplateValidationService.BodyStartBookmark, "1"),
            new Paragraph(
                new ParagraphProperties(
                    new ParagraphStyleId { Val = "Imported" },
                    new NumberingProperties(
                        new NumberingLevelReference { Val = 2 },
                        new NumberingId { Val = 41 }),
                    new SpacingBetweenLines { Before = "120", After = "80" }),
                new Run(
                    new Text(OrderedMarker + "Ordered item"),
                    new Drawing(new DW.Inline()))),
            new Paragraph(
                new ParagraphProperties(
                    new ParagraphStyleId { Val = "Imported" },
                    new Indentation { Left = "720" }),
                new Run(new Text(CodeMarker + "Code sample"))),
            new Paragraph(new Run(new Text(BoundaryMarker + "Import boundary"))),
            BookmarkParagraph(TemplateValidationService.BodyEndBookmark, "2")));
        mainPart.Document.Save();
        return path;
    }

    private static Paragraph BookmarkParagraph(string name, string id) => new(
        new BookmarkStart { Name = name, Id = id },
        new BookmarkEnd { Id = id });

    private static Paragraph AdmonitionParagraph(string content, string visualRole) => new(
        new ParagraphProperties(
            new ParagraphStyleId { Val = "Imported" },
            new SpacingBetweenLines { Before = "480", After = "480", Line = "360" },
            new Indentation { Left = "720", FirstLine = "360" },
            new Justification { Val = JustificationValues.Center }),
        new Run(
            new RunProperties(new RunFonts { Hint = FontTypeHintValues.EastAsia }),
            new Text(
                RoleMarker('c', StyleRoles.Admonition)
                + RoleMarker('d', visualRole)
                + content)));

    private static string RoleMarker(char markerIdCharacter, string role) =>
        $"__MD2WORD_ROLE_{new string(markerIdCharacter, 32)}_{role}__";

    private static TableCell SplitMarkerCell(string marker, string content) => new(
        new Paragraph(
            new Run(
                new RunProperties(new RunStyle { Val = "md2word-role-marker" }),
                new Text(marker[..^1])),
            new Run(
                new RunProperties(new RunStyle { Val = "md2word-role-marker" }),
                new LastRenderedPageBreak(),
                new Text(marker[^1..])),
            new Run(new Text(content))));
}
