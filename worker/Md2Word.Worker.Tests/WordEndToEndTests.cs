using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using V = DocumentFormat.OpenXml.Vml;

namespace Md2Word.Worker.Tests;

[Collection("Word COM E2E")]
public sealed class WordEndToEndTests
{
    [WordE2EFact]
    public async Task CancelDuringWordImportProducesCanceledTerminalResultAndStopsDedicatedWord()
    {
        var existingWordProcessIds = GetWordProcessIds();
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("Body", "正文", null),
            ("Ordered", "示例 有序列项", null),
            ("Code", "CodeBlock", null),
        ]);
        var css = workspace.WriteText("style.css", """
            p.manual-body-paragraph { mso-style-name: "正文"; }
            ol > li.manual-body-list-item { mso-style-name: "示例 有序列项"; }
            ul > li.manual-body-list-item { mso-style-name: "正文"; }
            p.manual-code-block-paragraph { mso-style-name: "CodeBlock"; }
            """);
        var markdownBuilder = new StringBuilder("# Synthetic Word cancellation\n\n");
        for (var index = 1; index <= 500; index++)
        {
            markdownBuilder.AppendLine($"Synthetic paragraph {index}. This fixture contains no private document content.");
            markdownBuilder.AppendLine();
        }
        var markdown = workspace.WriteText("cancel.md", markdownBuilder.ToString());
        var outputPath = workspace.PathFor("canceled-output.docx");
        var jobId = $"word-cancel-{Guid.NewGuid():N}";
        var requestId = $"request-{Guid.NewGuid():N}";
        var request = new ConversionRequest(
            jobId,
            "synthetic-template",
            markdown,
            outputPath,
            new TemplateSnapshot(
                template,
                css,
                TemplateValidationService.ComputeFingerprint(template, css)),
            new ToolPaths(Environment.GetEnvironmentVariable("MD2WORD_PANDOC_PATH") ?? "pandoc"),
            new ConversionOptions(3, "off", "png"));
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var convertFrame = JsonSerializer.Serialize(new
        {
            protocolVersion = "1.0",
            requestId,
            command = "convert",
            request,
        }, serializerOptions);
        var cancelFrame = JsonSerializer.Serialize(new
        {
            protocolVersion = "1.0",
            requestId,
            command = "cancel",
            jobId,
        }, serializerOptions);
        var protocolOutput = new WordImportObservingWriter();
        var protocolInput = new WordImportCancelReader(
            convertFrame,
            cancelFrame,
            protocolOutput.WordImportObserved,
            existingWordProcessIds);
        var diagnostics = new StringWriter();

        var exitCode = await WorkerProtocolHost.RunAsync(protocolInput, protocolOutput, diagnostics);

        Assert.True(protocolInput.DetectedDedicatedWordProcess,
            "The cancel frame must not be sent until the synthetic conversion has launched a new WINWORD process.");
        Assert.True(
            SpinWait.SpinUntil(
                () => GetWordProcessIds().All(processId => existingWordProcessIds.Contains(processId)),
                TimeSpan.FromSeconds(15)),
            "The dedicated canceled E2E WINWORD process did not exit after cancellation cleanup.");
        Assert.Equal(5, exitCode);
        Assert.False(File.Exists(outputPath));
        Assert.Empty(Directory.EnumerateDirectories(workspace.Root, $".md2word-worker-{jobId}-*"));

        var frames = protocolOutput.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();
        var eventFrames = frames
            .Where(frame => frame.GetProperty("type").GetString() == "event")
            .ToArray();
        var eventKinds = eventFrames
            .Select(frame => frame.GetProperty("event").GetProperty("kind").GetString())
            .ToArray();
        var wordImportIndex = Array.FindIndex(eventFrames, frame =>
            frame.GetProperty("event").GetProperty("kind").GetString() == "stage"
            && frame.GetProperty("event").GetProperty("stage").GetString() == "word-import");
        var cancelingIndex = Array.FindIndex(eventFrames, frame =>
            frame.GetProperty("event").GetProperty("kind").GetString() == "canceling");
        var canceledIndex = Array.FindIndex(eventFrames, frame =>
            frame.GetProperty("event").GetProperty("kind").GetString() == "canceled");

        Assert.True(wordImportIndex >= 0, "The synthetic conversion never entered word-import.");
        Assert.True(cancelingIndex > wordImportIndex, "The cancel request must follow the word-import stage.");
        Assert.True(canceledIndex > cancelingIndex, "The canceled terminal event must follow canceling.");
        Assert.DoesNotContain("completed", eventKinds);
        Assert.DoesNotContain("failed", eventKinds);
        Assert.DoesNotContain(frames, frame => frame.GetProperty("type").GetString() == "error");

        var canceledEvent = eventFrames[canceledIndex].GetProperty("event");
        Assert.Equal("cleanup", canceledEvent.GetProperty("stage").GetString());
        Assert.Equal("canceled", canceledEvent.GetProperty("result").GetProperty("status").GetString());
        var resultFrame = Assert.Single(frames, frame => frame.GetProperty("type").GetString() == "result");
        var result = resultFrame.GetProperty("result");
        Assert.Equal("canceled", result.GetProperty("status").GetString());
        Assert.False(result.TryGetProperty("outputPath", out _));
        Assert.Equal("result", frames[^1].GetProperty("type").GetString());
        Assert.DoesNotContain("WORD_AUTOMATION_FAILED", diagnostics.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_INTERNAL_ERROR", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [WordE2EFact]
    public void ConvertsSyntheticMarkdownWithNativeWordLists()
    {
        var existingWordProcessIds = GetWordProcessIds();
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("Body", "正文", null),
            ("Ordered", "示例 有序列项", null),
            ("Caption", "图注", null),
            ("Code", "CodeBlock", null),
        ]);
        var css = workspace.WriteText("style.css", """
            p.manual-body-paragraph { mso-style-name: "正文"; }
            p.manual-table-paragraph { mso-style-name: "正文"; }
            ol > li.manual-body-list-item { mso-style-name: "示例 有序列项"; }
            ul > li.manual-body-list-item { mso-style-name: "正文"; }
            p.manual-figure-caption { mso-style-name: "图注"; }
            p.manual-code-block-paragraph { mso-style-name: "CodeBlock"; }
            """);
          workspace.WriteBytes(
              "synthetic.png",
              Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAkAAAAFCAYAAACXU8ZrAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAWSURBVBhXY3DMfPifEGZAF8CG6awIAA9UckNw2x/IAAAAAElFTkSuQmCC"));
        var markdown = workspace.WriteText("synthetic.md", """
            ---
            heading_numbering: false
            ---

            # Synthetic list acceptance

            - dash bullet
            * star bullet
            + plus bullet

            4. ordered start four
               - nested bullet level two
                 3. nested ordered level three
                 4. nested ordered level three b
               - nested bullet return
            5. ordered after nested

            - bullet root
              2. ordered second level
                 + bullet third level
              3. ordered second after
            - bullet root after

            7. ordered loose before embedded

               loose continuation paragraph

               ```text
               list contained code
               ```

               ![synthetic pixel](synthetic.png){width=1800px height=1000px}

            8. ordered after embedded

            separator between ordered blocks

            4. ordered restarted four
            5. ordered restarted five

            ```c
            /* synthetic receive path */
            synthetic_ready(dev);
            if (ready) {
                synthetic_input(p);
            }
            ```
            """);
        var output = workspace.PathFor("output.docx");
        var fingerprint = TemplateValidationService.ComputeFingerprint(template, css);
        var request = new ConversionRequest(
            "e2e-job",
            "synthetic-template",
            markdown,
            output,
            new TemplateSnapshot(template, css, fingerprint),
            new ToolPaths(Environment.GetEnvironmentVariable("MD2WORD_PANDOC_PATH") ?? "pandoc"),
            new ConversionOptions(3, "off", "png"));

        var result = new ConversionService().Convert(request, (_, _) => { }, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.True(File.Exists(output));
        using var document = WordprocessingDocument.Open(output, false);
        var mainPart = document.MainDocumentPart!;
        var documentParts = new[] { (OpenXmlPart)mainPart }.Concat(EnumerateParts(mainPart)).ToArray();
        Assert.DoesNotContain(
            documentParts.SelectMany(part => part.ExternalRelationships),
            relationship => relationship.RelationshipType.EndsWith("/image", StringComparison.OrdinalIgnoreCase));
        var imageParts = mainPart.ImageParts.ToArray();
        Assert.NotEmpty(imageParts);
        Assert.All(imageParts, imagePart =>
        {
            using var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
            Assert.True(stream.Length > 0);
        });
        var imageRelationshipIds = imageParts
            .Select(mainPart.GetIdOfPart)
            .ToHashSet(StringComparer.Ordinal);
        var embeddedDrawingImages = mainPart.Document!.Body!.Descendants<A.Blip>()
            .Count(blip => imageRelationshipIds.Contains(blip.Embed?.Value ?? string.Empty));
        var embeddedVmlImages = mainPart.Document.Body.Descendants<V.ImageData>()
            .Count(image => imageRelationshipIds.Contains(image.RelationshipId?.Value ?? string.Empty));
        Assert.True(
            embeddedDrawingImages + embeddedVmlImages > 0,
            "The synthetic image was not backed by an embedded image part.");
        var inlineExtents = mainPart.Document.Body.Descendants<DW.Inline>()
            .Select(inline => inline.Extent)
            .Where(extent => extent?.Cx?.Value is > 0)
            .ToArray();
        Assert.NotEmpty(inlineExtents);
        Assert.All(
            inlineExtents,
            extent =>
            {
                Assert.True(
                    extent!.Cx!.Value <= 6_122_160L,
                    $"Inline image width {extent.Cx.Value} EMU exceeded the A4 body width.");
                Assert.True(extent.Cy?.Value is > 0);
                Assert.InRange((double)extent.Cx.Value / extent.Cy!.Value, 1.79d, 1.81d);
            });
        var imageParagraphs = mainPart.Document.Body.Descendants<Paragraph>()
            .Where(paragraph => paragraph.Descendants<Drawing>().Any())
            .ToArray();
        Assert.NotEmpty(imageParagraphs);
        Assert.All(
            imageParagraphs,
            paragraph => Assert.Equal(
                LineSpacingRuleValues.Auto,
                paragraph.ParagraphProperties?.SpacingBetweenLines?.LineRule?.Value));
        var paragraphs = mainPart.Document!.Body!.Descendants<Paragraph>().ToArray();
        var numbering = mainPart.NumberingDefinitionsPart!.Numbering!;
        var listParagraphs = paragraphs
            .Where(paragraph => paragraph.ParagraphProperties?.NumberingProperties is not null)
            .ToArray();
        Assert.NotEmpty(listParagraphs);

        var matrix = new[]
        {
            new ExpectedWordListParagraph("dash bullet", "正文", NumberFormatValues.Bullet, 0),
            new ExpectedWordListParagraph("star bullet", "正文", NumberFormatValues.Bullet, 0),
            new ExpectedWordListParagraph("plus bullet", "正文", NumberFormatValues.Bullet, 0),
            new ExpectedWordListParagraph("ordered start four", "示例 有序列项", NumberFormatValues.Decimal, 0),
            new ExpectedWordListParagraph("nested bullet level two", "正文", NumberFormatValues.Bullet, 1),
            new ExpectedWordListParagraph("nested ordered level three", "示例 有序列项", NumberFormatValues.Decimal, 2),
            new ExpectedWordListParagraph("nested ordered level three b", "示例 有序列项", NumberFormatValues.Decimal, 2),
            new ExpectedWordListParagraph("nested bullet return", "正文", NumberFormatValues.Bullet, 1),
            new ExpectedWordListParagraph("ordered after nested", "示例 有序列项", NumberFormatValues.Decimal, 0),
            new ExpectedWordListParagraph("bullet root", "正文", NumberFormatValues.Bullet, 0),
            new ExpectedWordListParagraph("ordered second level", "示例 有序列项", NumberFormatValues.Decimal, 1),
            new ExpectedWordListParagraph("bullet third level", "正文", NumberFormatValues.Bullet, 2),
            new ExpectedWordListParagraph("ordered second after", "示例 有序列项", NumberFormatValues.Decimal, 1),
            new ExpectedWordListParagraph("bullet root after", "正文", NumberFormatValues.Bullet, 0),
            new ExpectedWordListParagraph("ordered loose before embedded", "示例 有序列项", NumberFormatValues.Decimal, 0),
            new ExpectedWordListParagraph("ordered after embedded", "示例 有序列项", NumberFormatValues.Decimal, 0),
            new ExpectedWordListParagraph("ordered restarted four", "示例 有序列项", NumberFormatValues.Decimal, 0),
            new ExpectedWordListParagraph("ordered restarted five", "示例 有序列项", NumberFormatValues.Decimal, 0),
        };
        foreach (var expected in matrix)
        {
            var paragraph = FindParagraph(paragraphs, expected.Text);
            var properties = AssertNativeList(mainPart, paragraph, expected.StyleName, expected.Format, expected.Level, numbering);
            Assert.True(properties.NumberingId!.Val!.Value > 0);
        }

        Assert.All(listParagraphs, paragraph =>
        {
            var properties = paragraph.ParagraphProperties!.NumberingProperties!;
            var expectedStyleName = ResolveNumberFormat(numbering, properties) == NumberFormatValues.Bullet
                ? "正文"
                : "示例 有序列项";
            AssertParagraphStyle(mainPart, paragraph, expectedStyleName);
            Assert.NotNull(properties.NumberingId?.Val);
            Assert.NotNull(properties.NumberingLevelReference?.Val);
        });

        var dashNumberId = NumId(FindParagraph(paragraphs, "dash bullet"));
        Assert.Equal(dashNumberId, NumId(FindParagraph(paragraphs, "star bullet")));
        Assert.Equal(dashNumberId, NumId(FindParagraph(paragraphs, "plus bullet")));

        var expectedBulletLevels = new[]
        {
            (Text: "dash bullet", Glyph: "\uF06C", Left: "440"),
            (Text: "nested bullet level two", Glyph: "\uF06E", Left: "660"),
            (Text: "bullet third level", Glyph: "\uF075", Left: "880"),
        };
        foreach (var expected in expectedBulletLevels)
        {
            var level = ResolveLevel(numbering, FindNumbering(FindParagraph(paragraphs, expected.Text)));
            Assert.Equal(expected.Glyph, level.LevelText?.Val?.Value);
            Assert.Equal("Wingdings", level.NumberingSymbolRunProperties?.RunFonts?.Ascii?.Value);
            Assert.Equal(expected.Left, level.PreviousParagraphProperties?.Indentation?.Left?.Value);
            Assert.Equal("440", level.PreviousParagraphProperties?.Indentation?.Hanging?.Value);
        }

        var orderedRootNumberId = NumId(FindParagraph(paragraphs, "ordered start four"));
        Assert.Equal(orderedRootNumberId, NumId(FindParagraph(paragraphs, "nested bullet level two")));
        Assert.Equal(orderedRootNumberId, NumId(FindParagraph(paragraphs, "nested ordered level three")));
        Assert.Equal(orderedRootNumberId, NumId(FindParagraph(paragraphs, "ordered after nested")));
        Assert.Equal(4, ResolveStart(numbering, FindNumbering(FindParagraph(paragraphs, "ordered start four"))));
        Assert.Equal(3, ResolveStart(numbering, FindNumbering(FindParagraph(paragraphs, "nested ordered level three"))));

        var nestedOrderedNumberId = NumId(FindParagraph(paragraphs, "ordered second level"));
        Assert.Equal(nestedOrderedNumberId, NumId(FindParagraph(paragraphs, "bullet third level")));
        Assert.Equal(nestedOrderedNumberId, NumId(FindParagraph(paragraphs, "ordered second after")));
        Assert.Equal(2, ResolveStart(numbering, FindNumbering(FindParagraph(paragraphs, "ordered second level"))));

        var looseStart = FindParagraph(paragraphs, "ordered loose before embedded");
        var looseAfter = FindParagraph(paragraphs, "ordered after embedded");
        Assert.Equal(NumId(looseStart), NumId(looseAfter));
        Assert.Equal(7, ResolveStart(numbering, FindNumbering(looseStart)));

        var looseContinuation = FindParagraph(paragraphs, "loose continuation paragraph");
        AssertParagraphStyle(mainPart, looseContinuation, "示例 有序列项");
        Assert.Null(looseContinuation.ParagraphProperties?.NumberingProperties);
        Assert.Null(looseContinuation.ParagraphProperties?.Indentation);

        var codeParagraph = FindParagraph(paragraphs, "list contained code");
        AssertParagraphStyle(mainPart, codeParagraph, "CodeBlock");
        Assert.Null(codeParagraph.ParagraphProperties?.NumberingProperties);

        var multilineCodeParagraph = Assert.Single(
            paragraphs,
            paragraph => paragraph.InnerText.Contains("synthetic receive path", StringComparison.Ordinal));
        AssertParagraphStyle(mainPart, multilineCodeParagraph, "CodeBlock");
        Assert.Equal(4, multilineCodeParagraph.Descendants<Break>().Count());
        Assert.Contains("synthetic_ready(dev);", multilineCodeParagraph.InnerText, StringComparison.Ordinal);
        Assert.Contains("synthetic_input(p);", multilineCodeParagraph.InnerText, StringComparison.Ordinal);
        Assert.Contains(
            "    synthetic_input(p);",
            multilineCodeParagraph.InnerText.Replace('\u00A0', ' '),
            StringComparison.Ordinal);

        var restarted = FindParagraph(paragraphs, "ordered restarted four");
        Assert.NotEqual(orderedRootNumberId, NumId(restarted));
        Assert.Equal(4, ResolveStart(numbering, FindNumbering(restarted)));

        Assert.DoesNotContain(
            paragraphs,
            paragraph => paragraph.InnerText.Contains("__MD2WORD_ROLE_", StringComparison.Ordinal));

        Assert.True(
            SpinWait.SpinUntil(
                () => GetWordProcessIds().All(processId => existingWordProcessIds.Contains(processId)),
                TimeSpan.FromSeconds(5)),
            "The dedicated synthetic E2E WINWORD process did not exit after explicit COM cleanup.");
    }

    [WordE2EFact]
    public void AppliesFrontMatterHeadingNumberingAndRepeatTableHeaders()
    {
        var existingWordProcessIds = GetWordProcessIds();
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("Body", "正文", null),
            ("Ordered", "示例 有序列项", null),
            ("Code", "CodeBlock", null),
            ("H1", "合成标题1", null),
            ("H2", "合成标题2", null),
            ("H3", "合成标题3", null),
            ("H4", "合成标题4", null),
        ]);
        var css = workspace.WriteText("heading-style.css", """
            p.manual-body-paragraph { mso-style-name: "正文"; }
            p.manual-table-paragraph { mso-style-name: "正文"; }
            ol > li.manual-body-list-item { mso-style-name: "示例 有序列项"; }
            ul > li.manual-body-list-item { mso-style-name: "正文"; }
            p.manual-code-block-paragraph { mso-style-name: "CodeBlock"; }
            h1 { mso-style-name: "合成标题1"; }
            h2 { mso-style-name: "合成标题2"; }
            h3 { mso-style-name: "合成标题3"; }
            h4 { mso-style-name: "合成标题4"; }
            """);
        var fingerprint = TemplateValidationService.ComputeFingerprint(template, css);

        var enabledMarkdown = workspace.WriteText("heading-enabled.md", """
            ---
            heading_numbering_start_base: 2
            word_heading_numbering:
              level1:
                number_style: upper_roman
                format: "%1."
              level2:
                number_style: lower_letter
                format: "%1-%2"
              level3:
                number_style: upper_letter
                format: "%1-%2-%3"
              level4:
                number_style: lower_roman
                format: "%1-%2-%3-%4"
            ---

            # Synthetic summary

            ## Synthetic summary detail

            Synthetic unnumbered introduction.

            # Synthetic body

            Synthetic body introduction.

            ## Synthetic body detail

            Synthetic body detail paragraph.

            ### Synthetic body deeper

            Synthetic deeper paragraph.

            #### Synthetic body deepest

            | Header A | Header B |
            | --- | --- |
            | Value A | Value B |
            """);
        var enabledOutput = workspace.PathFor("heading-enabled.docx");
        var enabledResult = new ConversionService().Convert(
            new ConversionRequest(
                "heading-enabled-job",
                "synthetic-template",
                enabledMarkdown,
                enabledOutput,
                new TemplateSnapshot(template, css, fingerprint),
                new ToolPaths(Environment.GetEnvironmentVariable("MD2WORD_PANDOC_PATH") ?? "pandoc"),
                new ConversionOptions(3, "off", "png")),
            (_, _) => { },
            CancellationToken.None);

        Assert.Equal("succeeded", enabledResult.Status);
        using (var document = WordprocessingDocument.Open(enabledOutput, false))
        {
            var mainPart = document.MainDocumentPart!;
            var paragraphs = mainPart.Document!.Body!.Descendants<Paragraph>().ToArray();
            Assert.Null(FindParagraph(paragraphs, "Synthetic summary").ParagraphProperties?.NumberingProperties);
            Assert.Null(FindParagraph(paragraphs, "Synthetic summary detail").ParagraphProperties?.NumberingProperties);

            var numberedHeadings = new[]
            {
                (Text: "Synthetic body", StyleName: "合成标题1", Level: 0, Format: NumberFormatValues.UpperRoman, LevelText: "%1."),
                (Text: "Synthetic body detail", StyleName: "合成标题2", Level: 1, Format: NumberFormatValues.LowerLetter, LevelText: "%1-%2"),
                (Text: "Synthetic body deeper", StyleName: "合成标题3", Level: 2, Format: NumberFormatValues.UpperLetter, LevelText: "%1-%2-%3"),
                (Text: "Synthetic body deepest", StyleName: "合成标题4", Level: 3, Format: NumberFormatValues.LowerRoman, LevelText: "%1-%2-%3-%4"),
            };
            var numbering = mainPart.NumberingDefinitionsPart!.Numbering!;
            int? headingNumberId = null;
            foreach (var expected in numberedHeadings)
            {
                var paragraph = FindParagraph(paragraphs, expected.Text);
                AssertParagraphStyle(mainPart, paragraph, expected.StyleName);
                var properties = FindNumbering(paragraph);
                Assert.Equal(expected.Level, properties.NumberingLevelReference!.Val!.Value);
                Assert.Equal(expected.Format, ResolveNumberFormat(numbering, properties));
                Assert.Equal(expected.LevelText, ResolveLevel(numbering, properties).LevelText?.Val?.Value);
                headingNumberId ??= properties.NumberingId!.Val!.Value;
                Assert.Equal(headingNumberId, properties.NumberingId!.Val!.Value);
                Assert.All(paragraph.Descendants<RunProperties>(), runProperties =>
                {
                    Assert.Null(runProperties.RunFonts);
                    Assert.Null(runProperties.FontSize);
                    Assert.Null(runProperties.FontSizeComplexScript);
                    Assert.Null(runProperties.Kern);
                });
            }

            AssertValidBodyBookmarks(mainPart.Document.Body);

            var table = Assert.Single(mainPart.Document.Body.Descendants<Table>());
            Assert.NotNull(table.Elements<TableRow>().First().TableRowProperties?.GetFirstChild<TableHeader>());
        }

        var disabledMarkdown = workspace.WriteText("heading-disabled.md", """
            ---
            heading_numbering: false
            word_repeat_table_headers: false
            word_heading_numbering:
              level1:
                number_style: upper_roman
                format: "%1."
            ---

            # Synthetic unnumbered heading

            ## Synthetic unnumbered detail

            | Header A | Header B |
            | --- | --- |
            | Value A | Value B |
            """);
        var disabledOutput = workspace.PathFor("heading-disabled.docx");
        var disabledResult = new ConversionService().Convert(
            new ConversionRequest(
                "heading-disabled-job",
                "synthetic-template",
                disabledMarkdown,
                disabledOutput,
                new TemplateSnapshot(template, css, fingerprint),
                new ToolPaths(Environment.GetEnvironmentVariable("MD2WORD_PANDOC_PATH") ?? "pandoc"),
                new ConversionOptions(3, "off", "png")),
            (_, _) => { },
            CancellationToken.None);

        Assert.Equal("succeeded", disabledResult.Status);
        using (var document = WordprocessingDocument.Open(disabledOutput, false))
        {
            var body = document.MainDocumentPart!.Document!.Body!;
            var paragraphs = body.Descendants<Paragraph>().ToArray();
            Assert.Null(FindParagraph(paragraphs, "Synthetic unnumbered heading").ParagraphProperties?.NumberingProperties);
            Assert.Null(FindParagraph(paragraphs, "Synthetic unnumbered detail").ParagraphProperties?.NumberingProperties);
            var table = Assert.Single(body.Descendants<Table>());
            Assert.All(
                table.Elements<TableRow>(),
                row => Assert.Null(row.TableRowProperties?.GetFirstChild<TableHeader>()));
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => GetWordProcessIds().All(processId => existingWordProcessIds.Contains(processId)),
                TimeSpan.FromSeconds(10)),
            "The dedicated heading-numbering E2E WINWORD processes did not exit after explicit COM cleanup.");
    }

    [WordE2EFact]
    public void UsesWordNativeStylesWhenCssHasNoMappings()
    {
        var existingWordProcessIds = GetWordProcessIds();
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([]);
        var css = workspace.WriteText("native-fallback.css", "/* Deliberately no mso-style-name mappings. */");
        var markdown = workspace.WriteText("native-fallback.md", """
            ---
            heading_numbering: false
            ---

            # Native H1

            ## Native H2

            ### Native H3

            #### Native H4

            ##### Native H5

            ###### Native H6

            Native body with **strong** and *emphasis*.

            > NOTE: Native notice

            - Native bullet

            1. Native number

            ```text
            native code line 1
              native code line 2
            ```

            | Header | Value |
            | --- | --- |
            | A | B |

            : Synthetic table caption

            Term
            : Definition text
            """);
        var output = workspace.PathFor("native-fallback.docx");
        var result = new ConversionService().Convert(
            new ConversionRequest(
                "native-fallback-job",
                "synthetic-template",
                markdown,
                output,
                new TemplateSnapshot(template, css, TemplateValidationService.ComputeFingerprint(template, css)),
                new ToolPaths(Environment.GetEnvironmentVariable("MD2WORD_PANDOC_PATH") ?? "pandoc"),
                new ConversionOptions(3, "off", "png")),
            (_, _) => { },
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        ResaveDocumentWithWord(output);
        using (var document = WordprocessingDocument.Open(output, false))
        {
            var mainPart = document.MainDocumentPart!;
            var paragraphs = mainPart.Document!.Body!.Descendants<Paragraph>().ToArray();
            for (var level = 1; level <= 6; level++)
            {
                var heading = FindParagraph(paragraphs, $"Native H{level}");
                AssertParagraphStyle(mainPart, heading, $"heading {level}", $"标题 {level}", $"标题{level}");
                Assert.Null(heading.ParagraphProperties?.NumberingProperties);
            }

            AssertParagraphStyle(mainPart, FindParagraph(paragraphs, "Native body with strong and emphasis."), "Normal", "正文");
            AssertParagraphStyle(mainPart, FindParagraph(paragraphs, "NOTE Native notice"), "Intense Quote", "明显引用", "强烈引用");
            var code = Assert.Single(paragraphs, paragraph => paragraph.InnerText.Contains("native code line 1", StringComparison.Ordinal));
            Assert.Contains("native code line 2", code.InnerText, StringComparison.Ordinal);
            AssertParagraphStyle(mainPart, code, "No Spacing", "无间隔");
            AssertParagraphStyle(mainPart, FindParagraph(paragraphs, "Synthetic table caption"), "Caption", "caption", "图注", "题注");
            AssertParagraphStyle(mainPart, FindParagraph(paragraphs, "Term"), "Normal", "正文");
            AssertParagraphStyle(mainPart, FindParagraph(paragraphs, "Definition text"), "Normal", "正文");

            var numbering = mainPart.NumberingDefinitionsPart!.Numbering!;
            AssertNativeList(mainPart, FindParagraph(paragraphs, "Native bullet"), "List Paragraph", NumberFormatValues.Bullet, 0, numbering, "列表段落");
            AssertNativeList(mainPart, FindParagraph(paragraphs, "Native number"), "List Number", NumberFormatValues.Decimal, 0, numbering, "编号", "编号列表");

            var table = Assert.Single(mainPart.Document.Body.Descendants<Table>());
            Assert.All(
                table.Descendants<Paragraph>(),
                paragraph => AssertParagraphStyle(mainPart, paragraph, "Normal", "正文"));
            Assert.DoesNotContain(
                paragraphs,
                paragraph => paragraph.InnerText.Contains("__MD2WORD_ROLE_", StringComparison.Ordinal));
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => GetWordProcessIds().All(processId => existingWordProcessIds.Contains(processId)),
                TimeSpan.FromSeconds(20)),
            "The dedicated native-fallback E2E WINWORD process did not exit after explicit COM cleanup.");
    }

    [WordE2EFact]
    public void PreservesInternalLinksAsWordSafeBookmarks()
    {
        var existingWordProcessIds = GetWordProcessIds();
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([]);
        var css = workspace.WriteText("internal-links.css", "/* Use Word-native fallback styles. */");
        var markdown = workspace.WriteText("internal-links.md", """
            ---
            heading_numbering: false
            ---

            [Jump to the synthetic target](#中文目标)

            [Jump to the same target again](#中文目标)

            ## Synthetic internal-link target {#中文目标}

            Target body.
            """);
        var output = workspace.PathFor("internal-links.docx");

        var result = new ConversionService().Convert(
            new ConversionRequest(
                "internal-links-job",
                "synthetic-template",
                markdown,
                output,
                new TemplateSnapshot(template, css, TemplateValidationService.ComputeFingerprint(template, css)),
                new ToolPaths(Environment.GetEnvironmentVariable("MD2WORD_PANDOC_PATH") ?? "pandoc"),
                new ConversionOptions(3, "off", "png")),
            (_, _) => { },
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        using (var document = WordprocessingDocument.Open(output, false))
        {
            var body = document.MainDocumentPart!.Document!.Body!;
            var generatedBookmarks = body.Descendants<BookmarkStart>()
                .Where(bookmark => bookmark.Name?.Value is { } name
                    && name.StartsWith(
                        HtmlConversionService.InternalBookmarkPrefix,
                        StringComparison.Ordinal))
                .ToArray();
            var bookmark = Assert.Single(generatedBookmarks);
            var bookmarkName = Assert.IsType<string>(bookmark.Name?.Value);
            Assert.All(bookmarkName, character => Assert.InRange((int)character, 0, 127));

            var internalLinks = body.Descendants<Hyperlink>()
                .Where(link => link.Anchor?.Value is { } anchor
                    && anchor.StartsWith(
                        HtmlConversionService.InternalBookmarkPrefix,
                        StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, internalLinks.Length);
            Assert.All(internalLinks, link => Assert.Equal(bookmarkName, link.Anchor?.Value));
            Assert.DoesNotContain(
                body.Descendants<Hyperlink>(),
                link => string.Equals(link.Anchor?.Value, "中文目标", StringComparison.Ordinal));
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => GetWordProcessIds().All(processId => existingWordProcessIds.Contains(processId)),
                TimeSpan.FromSeconds(10)),
            "The dedicated internal-link E2E WINWORD process did not exit after explicit COM cleanup.");
    }

    [WordE2EFact]
    public void AppliesAdmonitionCalloutPaletteWithoutOverridingMappedWordStyle()
    {
        var existingWordProcessIds = GetWordProcessIds();
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("Body", "正文", null),
        ]);
        var css = workspace.WriteText("admonition-palette.css", """
            p.manual-body-paragraph,
            p.manual-admonition-paragraph { mso-style-name: "正文"; }
            """);
        var markdown = workspace.WriteText("admonition-palette.md", """
            # Synthetic callout palette

            > Synthetic generic quotation.

            > NOTE: Synthetic note.

            > CAUTION: Synthetic caution.

            > WARNING: Synthetic warning.

            > DANGER: Synthetic danger.
            """);
        var output = workspace.PathFor("admonition-palette.docx");

        var result = new ConversionService().Convert(
            new ConversionRequest(
                "admonition-palette-job",
                "synthetic-template",
                markdown,
                output,
                new TemplateSnapshot(template, css, TemplateValidationService.ComputeFingerprint(template, css)),
                new ToolPaths(Environment.GetEnvironmentVariable("MD2WORD_PANDOC_PATH") ?? "pandoc"),
                new ConversionOptions(3, "off", "png")),
            (_, _) => { },
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        using (var document = WordprocessingDocument.Open(output, false))
        {
            var mainPart = document.MainDocumentPart!;
            var paragraphs = mainPart.Document!.Body!.Descendants<Paragraph>().ToArray();
            var expected = new[]
            {
                ("Synthetic generic quotation.", "F8FAFC", "64748B"),
                ("Synthetic note.", "EFF6FF", "2563EB"),
                ("Synthetic caution.", "FFFBEB", "B45309"),
                ("Synthetic warning.", "FEF2F2", "B91C1C"),
                ("Synthetic danger.", "FEE2E2", "7F1D1D"),
            };
            foreach (var (text, fillColor, borderColor) in expected)
            {
                var paragraph = Assert.Single(
                    paragraphs,
                    candidate => candidate.InnerText.Contains(text, StringComparison.Ordinal));
                AssertParagraphStyle(mainPart, paragraph, "正文", "Normal");
                var properties = Assert.IsType<ParagraphProperties>(paragraph.ParagraphProperties);
                Assert.Null(properties.Indentation);
                Assert.Null(properties.SpacingBetweenLines);
                Assert.Null(properties.Justification);

                var shading = Assert.IsType<Shading>(properties.Shading);
                Assert.Equal(ShadingPatternValues.Clear, shading.Val?.Value);
                Assert.Equal(fillColor, shading.Fill?.Value);
                var borders = Assert.IsType<ParagraphBorders>(properties.ParagraphBorders);
                var left = Assert.IsType<LeftBorder>(borders.LeftBorder);
                Assert.Equal(BorderValues.Single, left.Val?.Value);
                Assert.Equal(borderColor, left.Color?.Value);
                Assert.Equal(18U, left.Size?.Value);
                Assert.Equal(0U, left.Space?.Value);
                Assert.Null(borders.TopBorder);
                Assert.Null(borders.RightBorder);
                Assert.Null(borders.BottomBorder);
            }
            Assert.DoesNotContain(
                paragraphs,
                paragraph => paragraph.InnerText.Contains("__MD2WORD_ROLE_", StringComparison.Ordinal));
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => GetWordProcessIds().All(processId => existingWordProcessIds.Contains(processId)),
                TimeSpan.FromSeconds(10)),
            "The dedicated admonition E2E WINWORD process did not exit after explicit COM cleanup.");
    }

    private static void ResaveDocumentWithWord(string path)
    {
        object? applicationObject = null;
        object? documentsObject = null;
        object? documentObject = null;
        var documentClosed = false;
        var applicationQuit = false;
        try
        {
            var applicationType = Type.GetTypeFromProgID("Word.Application")
                ?? throw new InvalidOperationException("Microsoft Word COM registration is unavailable.");
            applicationObject = Activator.CreateInstance(applicationType)
                ?? throw new InvalidOperationException("Microsoft Word could not be started.");
            dynamic application = applicationObject;
            application.Visible = false;
            application.DisplayAlerts = 0;
            documentsObject = application.Documents;
            dynamic documents = documentsObject;
            documentObject = documents.Open(
                FileName: path,
                ConfirmConversions: false,
                ReadOnly: false,
                AddToRecentFiles: false,
                Visible: false,
                OpenAndRepair: false);
            dynamic document = documentObject;
            document.Save();
            document.Close(0);
            documentClosed = true;
            application.Quit(0);
            applicationQuit = true;
        }
        finally
        {
            if (!documentClosed && documentObject is not null)
            {
                try
                {
                    ((dynamic)documentObject).Close(0);
                }
                catch
                {
                    // Best effort cleanup for the gated Word acceptance path.
                }
            }
            if (!applicationQuit && applicationObject is not null)
            {
                try
                {
                    ((dynamic)applicationObject).Quit(0);
                }
                catch
                {
                    // Best effort cleanup for the gated Word acceptance path.
                }
            }
            ReleaseComObject(documentObject);
            ReleaseComObject(documentsObject);
            ReleaseComObject(applicationObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [WordE2EFact]
    public void RebindsWordSavedStylesAndFinalizesMergedTableAndAdjacentTableSeparators()
    {
        var existingWordProcessIds = GetWordProcessIds();
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([
            ("MD2WordBody", "正文", null),
            ("MD2WordCaption", "图注", null),
        ]);
        var css = workspace.WriteText("word-renumbered-style.css", """
            p.manual-body-paragraph { mso-style-name: "正文"; }
            figcaption, p.manual-figure-caption { mso-style-name: "图注"; }
            """);
        workspace.WriteBytes(
            "synthetic.png",
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAkAAAAFCAYAAACXU8ZrAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAWSURBVBhXY3DMfPifEGZAF8CG6awIAA9UckNw2x/IAAAAAElFTkSuQmCC"));
        var markdown = workspace.WriteText("merged-table-and-caption.md", """
            # Synthetic chapter

            Synthetic body paragraph.

            ![Synthetic merged figure](synthetic.png)

            <table>
              <thead>
                <tr><th>Group</th><th>Byte</th><th>Name</th><th>Description</th></tr>
              </thead>
              <tbody>
                <tr><td rowspan="2">Header</td><td>1 ~ 4</td><td colspan="2">Combined description</td></tr>
                <tr><td>5 ~ 8</td><td>Type</td><td>Details</td></tr>
              </tbody>
            </table>

            | cmd | Description |
            | --- | --- |
            | 2 | Delete |

            | type | Description |
            | --- | --- |
            | 0 | Pset |

            | subtype | Description |
            | --- | --- |
            | 2 | Pset unit |
            """);
        var output = workspace.PathFor("word-renumbered-style.docx");

        var result = new ConversionService().Convert(
            new ConversionRequest(
                "word-renumbered-style-job",
                "synthetic-template",
                markdown,
                output,
                new TemplateSnapshot(template, css, TemplateValidationService.ComputeFingerprint(template, css)),
                new ToolPaths(Environment.GetEnvironmentVariable("MD2WORD_PANDOC_PATH") ?? "pandoc"),
                new ConversionOptions(3, "off", "png")),
            (_, _) => { },
            CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        using (var document = WordprocessingDocument.Open(output, false))
        {
            var mainPart = document.MainDocumentPart!;
            var body = mainPart.Document!.Body!;
            var caption = Assert.Single(
                body.Descendants<Paragraph>(),
                paragraph => paragraph.InnerText.Contains("Synthetic merged figure", StringComparison.Ordinal));
            var captionStyleId = Assert.IsType<string>(caption.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
            var captionStyle = Assert.Single(
                mainPart.StyleDefinitionsPart!.Styles!.Elements<Style>(),
                style => string.Equals(style.StyleId?.Value, captionStyleId, StringComparison.Ordinal));
            Assert.Contains(
                captionStyle.StyleName?.Val?.Value,
                new[] { "caption", "图注" },
                StringComparer.OrdinalIgnoreCase);

            var table = Assert.Single(
                body.Descendants<Table>(),
                candidate => candidate.Descendants<VerticalMerge>().Any()
                    && candidate.Descendants<GridSpan>().Any());
            var tableProperties = table.TableProperties!;
            Assert.Equal(TableWidthUnitValues.Dxa, tableProperties.TableWidth!.Type!.Value);
            Assert.Equal(TableLayoutValues.Autofit, tableProperties.TableLayout!.Type!.Value);
            Assert.Equal(6, tableProperties.TableBorders!.ChildElements.Count);
            Assert.Null(tableProperties.TableCellSpacing);
            Assert.Contains(
                table.Descendants<VerticalMerge>(),
                merge => merge.Val?.Value == MergedCellValues.Restart);
            Assert.Contains(
                table.Descendants<GridSpan>(),
                span => span.Val?.Value == 2);
            Assert.All(
                table.Descendants<TableCell>(),
                cell => Assert.Equal(
                    TableVerticalAlignmentValues.Center,
                    cell.TableCellProperties!.TableCellVerticalAlignment!.Val!.Value));

            var bodyParagraph = Assert.Single(
                body.Elements<Paragraph>(),
                paragraph => paragraph.InnerText.Contains("Synthetic body paragraph", StringComparison.Ordinal));
            var bodyStyleId = Assert.IsType<string>(
                bodyParagraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
            var bodyChildren = body.ChildElements.ToArray();
            var adjacentTables = new[] { "cmd", "type", "subtype" }
                .Select(label => Assert.Single(
                    body.Elements<Table>(),
                    candidate => string.Equals(
                        candidate.Elements<TableRow>().FirstOrDefault()
                            ?.Elements<TableCell>().FirstOrDefault()?.InnerText,
                        label,
                        StringComparison.Ordinal)))
                .ToArray();
            var tableIndexes = adjacentTables
                .Select(candidate => Array.IndexOf(bodyChildren, candidate))
                .ToArray();
            Assert.Equal(tableIndexes[0] + 2, tableIndexes[1]);
            Assert.Equal(tableIndexes[1] + 2, tableIndexes[2]);
            foreach (var separatorIndex in new[] { tableIndexes[0] + 1, tableIndexes[1] + 1 })
            {
                var separator = Assert.IsType<Paragraph>(bodyChildren[separatorIndex]);
                Assert.Equal(bodyStyleId, separator.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
                Assert.Single(separator.ParagraphProperties.ChildElements);
                Assert.Empty(separator.Descendants<Vanish>());
                Assert.Empty(separator.Elements<Run>());
            }
            Assert.DoesNotContain(
                mainPart.StyleDefinitionsPart!.Styles!.Elements<Style>(),
                style => (style.StyleName?.Val?.Value ?? string.Empty)
                    .Contains("separator", StringComparison.OrdinalIgnoreCase));
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => GetWordProcessIds().All(processId => existingWordProcessIds.Contains(processId)),
                TimeSpan.FromSeconds(10)),
            "The dedicated caption/merged-table E2E WINWORD process did not exit after explicit COM cleanup.");
    }

    private static HashSet<int> GetWordProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("WINWORD"))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }
        return ids;
    }

    private static IEnumerable<OpenXmlPart> EnumerateParts(OpenXmlPartContainer container) =>
        EnumerateParts(container, new HashSet<Uri>());

    private static IEnumerable<OpenXmlPart> EnumerateParts(OpenXmlPartContainer container, HashSet<Uri> visited)
    {
        foreach (var pair in container.Parts)
        {
            if (!visited.Add(pair.OpenXmlPart.Uri)) continue;
            yield return pair.OpenXmlPart;
            foreach (var nested in EnumerateParts(pair.OpenXmlPart, visited)) yield return nested;
        }
    }

    private static Paragraph FindParagraph(IEnumerable<Paragraph> paragraphs, string text)
    {
        var matches = paragraphs
            .Where(paragraph => string.Equals(paragraph.InnerText.Trim(), text, StringComparison.Ordinal))
            .ToArray();
        return Assert.Single(matches);
    }

    private static void AssertValidBodyBookmarks(Body body)
    {
        var starts = body.Descendants<BookmarkStart>().ToArray();
        var start = Assert.Single(starts, bookmark => bookmark.Name?.Value == TemplateValidationService.BodyStartBookmark);
        var end = Assert.Single(starts, bookmark => bookmark.Name?.Value == TemplateValidationService.BodyEndBookmark);
        Assert.True(Array.IndexOf(starts, start) < Array.IndexOf(starts, end));
        var ends = body.Descendants<BookmarkEnd>().Select(bookmark => bookmark.Id?.Value).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(start.Id?.Value, ends);
        Assert.Contains(end.Id?.Value, ends);
    }

    private static NumberingProperties AssertNativeList(
        MainDocumentPart mainPart,
        Paragraph paragraph,
        string styleName,
        NumberFormatValues format,
        int level,
        Numbering numbering,
        params string[] alternateStyleNames)
    {
        var properties = FindNumbering(paragraph);
        AssertParagraphStyle(mainPart, paragraph, [styleName, .. alternateStyleNames]);
        Assert.Equal(format, ResolveNumberFormat(numbering, properties));
        Assert.Equal(level, properties.NumberingLevelReference!.Val!.Value);
        return properties;
    }

    private static Style AssertParagraphStyle(
        MainDocumentPart mainPart,
        Paragraph paragraph,
        params string[] expectedNames)
    {
        var styleId = Assert.IsType<string>(paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        var style = Assert.Single(
            mainPart.StyleDefinitionsPart!.Styles!.Elements<Style>(),
            candidate => string.Equals(candidate.StyleId?.Value, styleId, StringComparison.Ordinal));
        var actualNames = new[] { style.StyleName?.Val?.Value }
            .Concat((style.Aliases?.Val?.Value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeStyleIdentity(value!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(
            expectedNames.Select(NormalizeStyleIdentity).Any(actualNames.Contains),
            $"Paragraph '{paragraph.InnerText}' used final style id '{styleId}' with name "
            + $"'{style.StyleName?.Val?.Value ?? "<none>"}', expected one of: {string.Join(", ", expectedNames)}.");
        return style;
    }

    private static string NormalizeStyleIdentity(string value)
    {
        var normalized = string.Concat(value.Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();
        return normalized switch
        {
            "NORMAL" or "正文" => "NORMAL",
            "CAPTION" or "图注" or "题注" => "CAPTION",
            "NOSPACING" or "无间隔" => "NOSPACING",
            "LISTPARAGRAPH" or "列表段落" => "LISTPARAGRAPH",
            "LISTNUMBER" or "编号" or "编号列表" => "LISTNUMBER",
            "INTENSEQUOTE" or "明显引用" or "强烈引用" => "INTENSEQUOTE",
            _ => normalized,
        };
    }

    private static NumberingProperties FindNumbering(Paragraph paragraph)
    {
        var properties = paragraph.ParagraphProperties?.NumberingProperties;
        Assert.True(
            properties is not null,
            $"Expected a native numbered paragraph: text={paragraph.InnerText}; "
            + $"style={paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "<none>"}.");
        Assert.NotNull(properties!.NumberingId?.Val);
        Assert.NotNull(properties.NumberingLevelReference?.Val);
        return properties;
    }

    private static int NumId(Paragraph paragraph) => FindNumbering(paragraph).NumberingId!.Val!.Value;

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

    private sealed record ExpectedWordListParagraph(
        string Text,
        string StyleName,
        NumberFormatValues Format,
        int Level);

    private sealed class WordImportObservingWriter : StringWriter
    {
        private readonly TaskCompletionSource wordImportObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WordImportObserved => wordImportObserved.Task;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            try
            {
                using var document = JsonDocument.Parse(value);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "event"
                    && root.TryGetProperty("event", out var conversionEvent)
                    && conversionEvent.TryGetProperty("kind", out var kind)
                    && kind.GetString() == "stage"
                    && conversionEvent.TryGetProperty("stage", out var stage)
                    && stage.GetString() == "word-import")
                {
                    wordImportObserved.TrySetResult();
                }
            }
            catch (JsonException)
            {
                // Protocol validity is asserted by parsing all frames after the host exits.
            }
        }
    }

    private sealed class WordImportCancelReader : TextReader
    {
        private readonly string convertFrame;
        private readonly string cancelFrame;
        private readonly Task wordImportObserved;
        private readonly IReadOnlySet<int> existingWordProcessIds;
        private int readCount;

        public WordImportCancelReader(
            string convertFrame,
            string cancelFrame,
            Task wordImportObserved,
            IReadOnlySet<int> existingWordProcessIds)
        {
            this.convertFrame = convertFrame;
            this.cancelFrame = cancelFrame;
            this.wordImportObserved = wordImportObserved;
            this.existingWordProcessIds = existingWordProcessIds;
        }

        public bool DetectedDedicatedWordProcess { get; private set; }

        public override Task<string?> ReadLineAsync()
        {
            Assert.Equal(1, Interlocked.Increment(ref readCount));
            return Task.FromResult<string?>(convertFrame);
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var currentRead = Interlocked.Increment(ref readCount);
            if (currentRead == 2)
            {
                await wordImportObserved.WaitAsync(cancellationToken);
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(20))
                {
                    if (GetWordProcessIds().Any(processId => !existingWordProcessIds.Contains(processId)))
                    {
                        DetectedDedicatedWordProcess = true;
                        return cancelFrame;
                    }
                    await Task.Delay(10, cancellationToken);
                }
                return cancelFrame;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }
}

internal sealed class WordE2EFactAttribute : FactAttribute
{
    public WordE2EFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MD2WORD_RUN_WORD_E2E"), "1", StringComparison.Ordinal))
        {
            Skip = "Set MD2WORD_RUN_WORD_E2E=1 to launch the registered Word COM server with synthetic fixtures.";
        }
    }
}
