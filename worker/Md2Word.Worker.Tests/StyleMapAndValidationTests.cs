using Md2Word.Worker.Services;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Md2Word.Worker.Tests;

public sealed class StyleMapAndValidationTests
{
    [Fact]
    public void ParsesTechnicalReportOrderedAndUnorderedMappings()
    {
        var css = """
            p.manual-body-paragraph { mso-style-name: "示例 正文"; }
            ol > li.manual-body-list-item { mso-style-name: "示例 有序列项"; }
            ul > li.manual-body-list-item { mso-style-name: "示例 正文"; }
            p.manual-code-block-paragraph { mso-style-name: "示例 代码"; }
            code.manual-inline-code { mso-style-name: "示例 正文"; }
            """;

        var map = StyleMapParser.Parse(css);

        Assert.Equal("示例 有序列项", map.RoleStyles[StyleRoles.OrderedList]);
        Assert.Equal("示例 正文", map.RoleStyles[StyleRoles.UnorderedList]);
        Assert.Equal("示例 正文", map.RoleStyles[StyleRoles.Body]);
        Assert.Equal("示例 正文", map.RoleStyles[StyleRoles.InlineCode]);
        Assert.Contains("li.manual-body-ordered-item", map.BuildCompatibilityCss());
        Assert.Contains("p.manual-body-unordered-item-paragraph", map.BuildCompatibilityCss());
    }

    [Fact]
    public void GenericListMappingFallsBackForBothKinds()
    {
        var map = StyleMapParser.Parse("""
            p.manual-body-paragraph { mso-style-name: "正文"; }
            li.manual-body-item, p.manual-body-item-paragraph { mso-style-name: "正文"; }
            p.manual-code-block-paragraph { mso-style-name: "CodeBlock"; }
            """);

        Assert.Equal("正文", map.RoleStyles[StyleRoles.OrderedList]);
        Assert.Equal("正文", map.RoleStyles[StyleRoles.UnorderedList]);
    }

    [Fact]
    public void ParsesExtendedBlockRoleMappings()
    {
        var map = StyleMapParser.Parse("""
            h5 { mso-style-name: "Heading Five"; }
            h6 { mso-style-name: "Heading Six"; }
            p.manual-admonition-paragraph { mso-style-name: "Notice"; }
            p.manual-table-caption { mso-style-name: "Table Caption"; }
            p.manual-figure-image-paragraph { mso-style-name: "Image Paragraph"; }
            """);

        Assert.Equal("Heading Five", map.RoleStyles[StyleRoles.Heading5]);
        Assert.Equal("Heading Six", map.RoleStyles[StyleRoles.Heading6]);
        Assert.Equal("Notice", map.RoleStyles[StyleRoles.Admonition]);
        Assert.Equal("Table Caption", map.RoleStyles[StyleRoles.TableCaption]);
        Assert.Equal("Image Paragraph", map.RoleStyles[StyleRoles.FigureImage]);
    }

    [Fact]
    public void UsesWordNativeParagraphStylesWhenCssOmitsMappings()
    {
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([]);
        var css = workspace.WriteText("style.css", "/* No mso-style-name mappings. */");

        var outcome = new TemplateValidationService().Validate(template, css);

        Assert.Equal("valid", outcome.Report.Status);
        Assert.DoesNotContain(outcome.Report.Issues, issue => issue.Code == "CSS_STYLE_MAPPING_MISSING");
        Assert.Equal("Normal", outcome.ResolvedRoleStyles[StyleRoles.Body].StyleId);
        Assert.Equal("ListNumber", outcome.ResolvedRoleStyles[StyleRoles.OrderedList].StyleId);
        Assert.Equal("ListParagraph", outcome.ResolvedRoleStyles[StyleRoles.UnorderedList].StyleId);
        Assert.Equal("Heading1", outcome.ResolvedRoleStyles[StyleRoles.Heading1].StyleId);
        Assert.Equal("Heading6", outcome.ResolvedRoleStyles[StyleRoles.Heading6].StyleId);
        Assert.Equal("Caption", outcome.ResolvedRoleStyles[StyleRoles.Caption].StyleId);
        Assert.Equal("Caption", outcome.ResolvedRoleStyles[StyleRoles.TableCaption].StyleId);
        Assert.Equal("NoSpacing", outcome.ResolvedRoleStyles[StyleRoles.CodeBlock].StyleId);
        Assert.Equal("Normal", outcome.ResolvedRoleStyles[StyleRoles.InlineCode].StyleId);
        Assert.Equal("Normal", outcome.ResolvedRoleStyles[StyleRoles.Table].StyleId);
        Assert.Equal("IntenseQuote", outcome.ResolvedRoleStyles[StyleRoles.Admonition].StyleId);
        Assert.Equal("Normal", outcome.ResolvedRoleStyles[StyleRoles.FigureImage].StyleId);
        Assert.All(outcome.Report.StyleMappings, mapping => Assert.Equal("word-fallback", mapping.Status));
    }

    [Fact]
    public void ResolvesLocalizedCssNameAgainstEquivalentBuiltInWordStyle()
    {
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([]);
        var css = workspace.WriteText("style.css", """
            p.manual-body-paragraph { mso-style-name: "正文"; }
            """);

        var outcome = new TemplateValidationService().Validate(template, css);

        Assert.Equal("valid", outcome.Report.Status);
        Assert.DoesNotContain(outcome.Report.Issues, issue => issue.Code == "WORD_STYLE_MISSING");
        Assert.Equal("Normal", outcome.ResolvedRoleStyles[StyleRoles.Body].StyleId);
        Assert.Equal("Normal", outcome.ResolvedRoleStyles[StyleRoles.Body].Name);
        var mapping = Assert.Single(outcome.Report.StyleMappings, mapping => mapping.Role == "body");
        Assert.Equal("正文", mapping.RequestedStyleName);
        Assert.Equal("Normal", mapping.ResolvedStyleId);
        Assert.Equal("resolved", mapping.Status);
    }

    [Fact]
    public void PrefersExactStyleNameOverBuiltInEquivalent()
    {
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("LocalizedBody", "正文", null),
        ]);
        var css = workspace.WriteText("style.css", """
            p.manual-body-paragraph { mso-style-name: "正文"; }
            """);

        var outcome = new TemplateValidationService().Validate(template, css);

        Assert.Equal("valid", outcome.Report.Status);
        Assert.Equal("LocalizedBody", outcome.ResolvedRoleStyles[StyleRoles.Body].StyleId);
    }

    [Fact]
    public void TreatsAnUnavailableHeadingLevelAsConditionalUntilTheSourceUsesIt()
    {
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([]);
        using (var document = WordprocessingDocument.Open(template, true))
        {
            var styles = document.MainDocumentPart?.StyleDefinitionsPart?.Styles
                ?? throw new InvalidDataException("Synthetic template styles are missing.");
            styles.Elements<Style>()
                .Single(style => string.Equals(style.StyleId?.Value, "Heading6", StringComparison.Ordinal))
                .Remove();
            styles.Save();
        }
        var css = workspace.WriteText("style.css", "/* No h6 mapping. */");

        var outcome = new TemplateValidationService().Validate(template, css);

        Assert.Equal("warning", outcome.Report.Status);
        var issue = Assert.Single(outcome.Report.Issues, issue => issue.Code == "WORD_NATIVE_STYLE_FALLBACK_MISSING");
        Assert.Equal("warning", issue.Severity);
        Assert.Contains("source documents that do not produce h6", issue.Message, StringComparison.Ordinal);
        Assert.False(outcome.ResolvedRoleStyles.ContainsKey(StyleRoles.Heading6));
        var mapping = Assert.Single(
            outcome.Report.StyleMappings,
            mapping => mapping.Role == "heading" && mapping.HeadingLevel == 6);
        Assert.Equal("not-configured", mapping.Status);
    }

    [Fact]
    public void ValidatesBookmarksStylesAliasesAndFingerprint()
    {
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("BodyStyle", "示例 正文", "正文别名"),
            ("OrderedStyle", "示例 有序列项", null),
            ("CodeStyle", "示例 代码", null),
        ]);
        var css = workspace.WriteText("style.css", """
            p.manual-body-paragraph { mso-style-name: "正文别名"; }
            ol > li.manual-body-list-item { mso-style-name: "示例 有序列项"; }
            ul > li.manual-body-list-item { mso-style-name: "示例 正文"; }
            p.manual-code-block-paragraph { mso-style-name: "示例 代码"; }
            """);

        var first = new TemplateValidationService().Validate(template, css);
        Assert.Equal("valid", first.Report.Status);
        Assert.Equal("BodyStyle", first.ResolvedRoleStyles[StyleRoles.Body].StyleId);
        Assert.Equal("BodyStyle", first.ResolvedRoleStyles[StyleRoles.InlineCode].StyleId);
        Assert.Equal("sha256:", first.Report.ContentFingerprint[..7]);

        File.AppendAllText(css, Environment.NewLine + "/* fingerprint change */");
        var second = new TemplateValidationService().Validate(template, css);
        Assert.NotEqual(first.Report.ContentFingerprint, second.Report.ContentFingerprint);
    }

    [Fact]
    public void ReportsMissingReferencedStyleAndInvalidBookmarkOrder()
    {
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("BodyStyle", "正文", null),
            ("CodeStyle", "CodeBlock", null),
        ], validBodyOrder: false);
        var css = workspace.WriteText("style.css", """
            p.manual-body-paragraph { mso-style-name: "正文"; }
            li.manual-body-item { mso-style-name: "正文"; }
            p.manual-code-block-paragraph { mso-style-name: "CodeBlock"; }
            h1 { mso-style-name: "不存在标题"; }
            """);

        var report = new TemplateValidationService().Validate(template, css).Report;

        Assert.Equal("invalid", report.Status);
        Assert.Contains(report.Issues, issue => issue.Code == "BODY_BOOKMARK_ORDER_INVALID");
        Assert.Contains(report.Issues, issue => issue.Code == "WORD_STYLE_MISSING");
    }

    [Fact]
    public void RejectsExternalRelationshipsAndDangerousFieldsInTemplates()
    {
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("BodyStyle", "正文", null),
            ("CodeStyle", "CodeBlock", null),
        ]);
        using (var document = WordprocessingDocument.Open(template, true))
        {
            var mainPart = document.MainDocumentPart!;
            mainPart.AddExternalRelationship(
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
                new Uri("https://example.invalid/external.png"),
                "rIdExternalImage");
            var body = mainPart.Document?.Body ?? throw new InvalidDataException("Synthetic template body missing.");
            body.Append(new Paragraph(
                new SimpleField { Instruction = " PAGE " },
                new SimpleField { Instruction = " DDEAUTO synthetic command " }));
            mainPart.Document.Save();
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

    [Fact]
    public void RejectsDangerousComplexFieldInstructionsSplitAcrossRuns()
    {
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("BodyStyle", "正文", null),
            ("CodeStyle", "CodeBlock", null),
        ]);
        using (var document = WordprocessingDocument.Open(template, true))
        {
            var body = document.MainDocumentPart?.Document?.Body
                ?? throw new InvalidDataException("Synthetic template body missing.");
            body.Append(new Paragraph(
                new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                new Run(new FieldCode("D")),
                new Run(new FieldCode("DEAUTO synthetic command")),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
                new Run(new Text("synthetic result")),
                new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
            document.MainDocumentPart!.Document.Save();
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
}
