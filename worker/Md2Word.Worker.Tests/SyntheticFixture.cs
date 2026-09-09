using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

internal sealed class SyntheticWorkspace : IDisposable
{
    private const string RoleMarkerPrefix = "__MD2WORD_ROLE_0123456789abcdef0123456789abcdef_";

    public SyntheticWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "md2word-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string PathFor(string fileName) => Path.Combine(Root, fileName);

    public string WriteText(string fileName, string content)
    {
        var path = PathFor(fileName);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return path;
    }

    public string WriteBytes(string fileName, byte[] content)
    {
        var path = PathFor(fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string CreateTemplate(
        IEnumerable<(string Id, string Name, string? Aliases)> styles,
        bool validBodyOrder = true,
        bool includeBodyStart = true,
        bool includeBodyEnd = true,
        bool includeOptionalCover = true)
    {
        var path = PathFor("template.docx");
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = package.AddMainDocumentPart();
        var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylePart.Styles = new Styles();
        var configuredStyles = styles.ToArray();
        var configuredIds = configuredStyles.Select(style => style.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configuredNames = configuredStyles.Select(style => style.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var style in NativeFallbackStyles().Where(style =>
            !configuredIds.Contains(style.StyleId?.Value ?? string.Empty)
            && !configuredNames.Contains(style.StyleName?.Val?.Value ?? string.Empty)))
        {
            stylePart.Styles.Append(style);
        }
        foreach (var (id, name, aliases) in configuredStyles)
        {
            var style = new Style { Type = StyleValues.Paragraph, StyleId = id };
            style.Append(new StyleName { Val = name });
            if (!string.IsNullOrWhiteSpace(aliases))
            {
                style.Append(new Aliases { Val = aliases });
            }
            stylePart.Styles.Append(style);
        }
        stylePart.Styles.Save();

        var body = new Body();
        if (includeOptionalCover)
        {
            body.Append(BookmarkParagraph("MANUAL_COVER_TITLE", "10"));
            body.Append(BookmarkParagraph("MANUAL_COVER_SUBTITLE", "11"));
        }
        if (validBodyOrder)
        {
            if (includeBodyStart) body.Append(BookmarkParagraph("MANUAL_BODY_START", "1"));
            body.Append(new Paragraph(new Run(new Text("synthetic placeholder"))));
            if (includeBodyEnd) body.Append(BookmarkParagraph("MANUAL_BODY_END", "2"));
        }
        else
        {
            if (includeBodyEnd) body.Append(BookmarkParagraph("MANUAL_BODY_END", "2"));
            body.Append(new Paragraph(new Run(new Text("synthetic placeholder"))));
            if (includeBodyStart) body.Append(BookmarkParagraph("MANUAL_BODY_START", "1"));
        }
        mainPart.Document = new Document(body);
        mainPart.Document.Save();
        return path;
    }

    public string CreateListDocument(
        bool includeNestedMixed = false,
        bool includeContinuation = false,
        bool includeRoleMarkers = false)
    {
        var path = PathFor("lists.docx");
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = package.AddMainDocumentPart();
        var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylePart.Styles = new Styles(
            ParagraphStyle("Imported", "manual-body-item"),
            ParagraphStyle("ExampleOrdered", "示例 有序列项"),
            ParagraphStyle("ExampleBody", "示例 正文"));
        stylePart.Styles.Save();

        var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
        var bullet = new AbstractNum { AbstractNumberId = 1 };
        bullet.Append(LevelDefinition(0, NumberFormatValues.Bullet, 1));
        var decimalNumber = new AbstractNum { AbstractNumberId = 2 };
        decimalNumber.Append(LevelDefinition(0, NumberFormatValues.Decimal, 3));
        decimalNumber.Append(LevelDefinition(1, NumberFormatValues.Bullet, 1));
        numberingPart.Numbering = new Numbering(
            bullet,
            decimalNumber,
            NumberInstance(1, 1),
            NumberInstance(2, 2));
        numberingPart.Numbering.Save();

        var body = new Body();
        body.Append(ListParagraph("outside-before", 2, 0, spacing: true));
        body.Append(BookmarkParagraph("MANUAL_BODY_START", "1"));
        body.Append(ListParagraph(RoleMarked("ordered-a", includeRoleMarkers), 2, 0, spacing: true));
        if (includeContinuation)
        {
            body.Append(ContinuationParagraph(RoleMarked("ordered continuation", includeRoleMarkers)));
        }
        if (includeNestedMixed)
        {
            body.Append(ListParagraph("nested-bullet", 2, 1, spacing: true));
        }
        body.Append(ListParagraph(RoleMarked("ordered-b", includeRoleMarkers), 2, 0, spacing: false));
        body.Append(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "ExampleBody" }), new Run(new Text("separator"))));
        body.Append(ListParagraph(RoleMarked("ordered-new-block", includeRoleMarkers), 2, 0, spacing: true));
        body.Append(ListParagraph("bullet", 1, 0, spacing: true));
        body.Append(BookmarkParagraph("MANUAL_BODY_END", "2"));
        body.Append(ListParagraph("outside-after", 2, 0, spacing: true));
        mainPart.Document = new Document(body);
        mainPart.Document.Save();
        return path;
    }

    public string CreateListAcceptanceDocument()
    {
        var path = PathFor("list-acceptance.docx");
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = package.AddMainDocumentPart();
        var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylePart.Styles = new Styles(
            ParagraphStyle("Imported", "manual-body-item"),
            ParagraphStyle("Code", "CodeBlock"),
            ParagraphStyle("ExampleOrdered", "示例 有序列项"),
            ParagraphStyle("ExampleBody", "示例 正文"));
        stylePart.Styles.Save();

        var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
        var orderedMixed = new AbstractNum { AbstractNumberId = 10 };
        orderedMixed.Append(LevelDefinition(0, NumberFormatValues.Decimal, 4));
        orderedMixed.Append(LevelDefinition(1, NumberFormatValues.Bullet, 1));
        orderedMixed.Append(LevelDefinition(2, NumberFormatValues.LowerLetter, 2));
        var unorderedMixed = new AbstractNum { AbstractNumberId = 20 };
        unorderedMixed.Append(LevelDefinition(0, NumberFormatValues.Bullet, 1));
        unorderedMixed.Append(LevelDefinition(1, NumberFormatValues.Decimal, 2));
        unorderedMixed.Append(LevelDefinition(2, NumberFormatValues.Bullet, 1));
        numberingPart.Numbering = new Numbering(
            orderedMixed,
            unorderedMixed,
            NumberInstance(10, 10),
            NumberInstance(20, 20));
        numberingPart.Numbering.Save();

        var body = new Body();
        body.Append(ListParagraph("outside-ordered", 10, 0, spacing: true));
        body.Append(BookmarkParagraph("MANUAL_BODY_START", "1"));

        body.Append(ListParagraph("ordered-root-a", 10, 0, spacing: true));
        body.Append(ContinuationParagraph(RoleMarked("ordered loose continuation a", StyleRoles.OrderedList)));
        body.Append(ContinuationParagraph(RoleMarked("ordered loose continuation b", StyleRoles.OrderedList)));
        body.Append(ContinuationParagraph(
            RoleMarked("ordered embedded code", StyleRoles.CodeBlock, StyleRoles.OrderedList),
            "Code"));
        body.Append(ContinuationParagraph(RoleMarked("ordered embedded image", StyleRoles.OrderedList)));
        body.Append(ListParagraph("ordered-nested-bullet", 10, 1, spacing: true));
        body.Append(ListParagraph("ordered-third-level", 10, 2, spacing: true));
        body.Append(ListParagraph("ordered-nested-bullet-return", 10, 1, spacing: true));
        body.Append(ListParagraph("ordered-root-b", 10, 0, spacing: true));

        body.Append(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "ExampleBody" }),
            new Run(new Text("ordered separator"))));
        body.Append(ListParagraph("ordered-restart-four", 10, 0, spacing: true));

        body.Append(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "ExampleBody" }),
            new Run(new Text("unordered separator"))));
        body.Append(ListParagraph("bullet-root-a", 20, 0, spacing: true));
        body.Append(ListParagraph("bullet-nested-ordered-a", 20, 1, spacing: true));
        body.Append(ListParagraph("bullet-third-level", 20, 2, spacing: true));
        body.Append(ListParagraph("bullet-nested-ordered-b", 20, 1, spacing: true));
        body.Append(ListParagraph("bullet-root-b", 20, 0, spacing: true));

        body.Append(BookmarkParagraph("MANUAL_BODY_END", "2"));
        body.Append(ListParagraph("outside-bullet", 20, 0, spacing: true));
        mainPart.Document = new Document(body);
        mainPart.Document.Save();
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Test temp cleanup is best effort.
        }
    }

    private static Paragraph BookmarkParagraph(string name, string id) => new(
        new BookmarkStart { Name = name, Id = id },
        new BookmarkEnd { Id = id });

    private static Style ParagraphStyle(string id, string name) => new(new StyleName { Val = name })
    {
        Type = StyleValues.Paragraph,
        StyleId = id,
    };

    private static IEnumerable<Style> NativeFallbackStyles()
    {
        yield return new Style(
            new StyleName { Val = "Normal" },
            new PrimaryStyle())
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true,
        };

        for (var level = 1; level <= 6; level++)
        {
            yield return new Style(
                new StyleName { Val = $"heading {level}" },
                new BasedOn { Val = "Normal" },
                new PrimaryStyle(),
                new StyleParagraphProperties(new OutlineLevel { Val = level - 1 }))
            {
                Type = StyleValues.Paragraph,
                StyleId = $"Heading{level}",
            };
        }

        foreach (var (id, name) in new[]
        {
            ("Caption", "caption"),
            ("NoSpacing", "No Spacing"),
            ("ListParagraph", "List Paragraph"),
            ("ListNumber", "List Number"),
            ("ListBullet", "List Bullet"),
            ("Quote", "Quote"),
            ("IntenseQuote", "Intense Quote"),
        })
        {
            yield return new Style(
                new StyleName { Val = name },
                new BasedOn { Val = "Normal" },
                new PrimaryStyle())
            {
                Type = StyleValues.Paragraph,
                StyleId = id,
            };
        }
    }

    private static Level LevelDefinition(int index, NumberFormatValues format, int start) => new(
        new StartNumberingValue { Val = start },
        new NumberingFormat { Val = format },
        new LevelText { Val = format == NumberFormatValues.Bullet ? "•" : $"%{index + 1}." })
    {
        LevelIndex = index,
    };

    private static NumberingInstance NumberInstance(int numberId, int abstractId) => new(new AbstractNumId { Val = abstractId })
    {
        NumberID = numberId,
    };

    private static Paragraph ListParagraph(string text, int numberId, int level, bool spacing)
    {
        var properties = new ParagraphProperties(
            new ParagraphStyleId { Val = "Imported" },
            new NumberingProperties(
                new NumberingLevelReference { Val = level },
                new NumberingId { Val = numberId }));
        if (spacing) properties.Append(new SpacingBetweenLines { Before = "120", After = "80" });
        return new Paragraph(properties, new Run(new Text(text)));
    }

    private static Paragraph ContinuationParagraph(string text, string styleId = "manual-body-item-paragraph") => new(
        new ParagraphProperties(
            new ParagraphStyleId { Val = styleId },
            new Indentation { Left = "720" },
            new SpacingBetweenLines { Before = "120" }),
        new Run(new Text(text)));

    private static string RoleMarked(string text, bool includeRoleMarker) =>
        includeRoleMarker ? RoleMarked(text, StyleRoles.OrderedList) : text;

    private static string RoleMarked(string text, params string[] roles) =>
        string.Concat(roles.Select(role => $"{RoleMarkerPrefix}{role}__")) + text;
}
