using System.Text;
using System.Text.RegularExpressions;

namespace Md2Word.Worker.Services;

internal static partial class StyleRoles
{
    public const string Body = "body";
    public const string OrderedList = "ordered-list";
    public const string UnorderedList = "unordered-list";
    public const string Heading1 = "h1";
    public const string Heading2 = "h2";
    public const string Heading3 = "h3";
    public const string Heading4 = "h4";
    public const string Heading5 = "h5";
    public const string Heading6 = "h6";
    public const string Caption = "caption";
    public const string TableCaption = "table-caption";
    public const string CodeBlock = "code-block";
    public const string InlineCode = "inline-code";
    public const string Table = "table";
    public const string Admonition = "admonition";
    public const string FigureImage = "figure-image";

    public static readonly string[] All =
    [
        Body,
        OrderedList,
        UnorderedList,
        Heading1,
        Heading2,
        Heading3,
        Heading4,
        Heading5,
        Heading6,
        Caption,
        TableCaption,
        CodeBlock,
        InlineCode,
        Table,
        Admonition,
        FigureImage,
    ];

    public static IReadOnlyList<string> WordNativeFallbackNames(string role) => role switch
    {
        Body => ["Normal", "正文"],
        OrderedList => ["List Number", "编号", "List Paragraph", "列表段落"],
        // Word's Bullets command keeps the current paragraph style (or uses
        // List Paragraph) and applies numbering separately. Preferring the
        // List Bullet style would add its own numbering definition and can
        // make generated indentation differ from a manually inserted list.
        UnorderedList => ["List Paragraph", "列表段落", "List Bullet", "项目符号"],
        Heading1 => HeadingFallbackNames(1),
        Heading2 => HeadingFallbackNames(2),
        Heading3 => HeadingFallbackNames(3),
        Heading4 => HeadingFallbackNames(4),
        Heading5 => HeadingFallbackNames(5),
        Heading6 => HeadingFallbackNames(6),
        Caption or TableCaption => ["Caption", "caption", "题注", "图注"],
        CodeBlock => ["CodeBlock", "Code Block", "代码块", "No Spacing", "无间隔"],
        // Inline code is projected from a Word paragraph style onto the run.
        // With no explicit CSS mapping it therefore inherits the document's
        // native body style instead of retaining Word's transient HTML Code
        // character style.
        InlineCode => ["Normal", "正文"],
        Admonition => ["Intense Quote", "明显引用", "Quote", "引用"],
        Table or FigureImage => [],
        _ => [],
    };

    public static bool RequiresSemanticWordFallback(string role) => role is
        Heading1 or Heading2 or Heading3 or Heading4 or Heading5 or Heading6;

    private static string[] HeadingFallbackNames(int level) =>
        [$"Heading {level}", $"heading {level}", $"标题 {level}", $"标题{level}"];
}

internal sealed record CssStyleReference(string Selector, string StyleName, string? Role);

internal sealed class ParsedStyleMap
{
    public ParsedStyleMap(
        IReadOnlyDictionary<string, string> roleStyles,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleSelectors,
        IReadOnlyList<CssStyleReference> references)
    {
        RoleStyles = roleStyles;
        RoleSelectors = roleSelectors;
        References = references;
    }

    public IReadOnlyDictionary<string, string> RoleStyles { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RoleSelectors { get; }
    public IReadOnlyList<CssStyleReference> References { get; }

    public string BuildCompatibilityCss()
    {
        var builder = new StringBuilder("/* MD2Word Word list compatibility selectors. */\n");
        if (RoleStyles.TryGetValue(StyleRoles.OrderedList, out var ordered))
        {
            builder.AppendLine("li.manual-body-ordered-item,");
            builder.AppendLine("p.manual-body-ordered-item-paragraph {");
            builder.Append("  mso-style-name: \"").Append(EscapeCssString(ordered)).AppendLine("\";");
            builder.AppendLine("}");
        }
        if (RoleStyles.TryGetValue(StyleRoles.UnorderedList, out var unordered))
        {
            builder.AppendLine("li.manual-body-unordered-item,");
            builder.AppendLine("p.manual-body-unordered-item-paragraph {");
            builder.Append("  mso-style-name: \"").Append(EscapeCssString(unordered)).AppendLine("\";");
            builder.AppendLine("}");
        }
        return builder.ToString();
    }

    private static string EscapeCssString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

internal static partial class StyleMapParser
{
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.Singleline)]
    private static partial Regex RuleRegex();

    [GeneratedRegex("""\bmso-style-name\s*:\s*(?:"(?<double>[^"]+)"|'(?<single>[^']+)'|(?<bare>[^;]+))\s*;?""", RegexOptions.IgnoreCase)]
    private static partial Regex MsoStyleRegex();

    public static ParsedStyleMap ParseFile(string cssPath) => Parse(File.ReadAllText(cssPath, Encoding.UTF8));

    public static ParsedStyleMap Parse(string css)
    {
        var cleanCss = CommentRegex().Replace(css, string.Empty);
        var roleStyles = new Dictionary<string, string>(StringComparer.Ordinal);
        var roleSelectors = StyleRoles.All.ToDictionary(role => role, _ => new List<string>(), StringComparer.Ordinal);
        var references = new List<CssStyleReference>();
        string? genericListStyle = null;
        var genericListSelectors = new List<string>();

        foreach (Match rule in RuleRegex().Matches(cleanCss))
        {
            var styleMatch = MsoStyleRegex().Match(rule.Groups["body"].Value);
            if (!styleMatch.Success)
            {
                continue;
            }
            var styleName = (styleMatch.Groups["double"].Value
                + styleMatch.Groups["single"].Value
                + styleMatch.Groups["bare"].Value).Trim();
            if (string.IsNullOrWhiteSpace(styleName))
            {
                continue;
            }

            foreach (var rawSelector in rule.Groups["selectors"].Value.Split(','))
            {
                var selector = NormalizeSelector(rawSelector);
                if (selector.Length == 0 || selector.StartsWith('@'))
                {
                    continue;
                }
                var role = ResolveRole(selector, out var isGenericList);
                references.Add(new CssStyleReference(selector, styleName, role));
                if (isGenericList)
                {
                    genericListStyle = styleName;
                    genericListSelectors.Add(selector);
                }
                if (role is null)
                {
                    continue;
                }
                roleStyles[role] = styleName;
                roleSelectors[role].Add(selector);
            }
        }

        if (genericListStyle is not null)
        {
            foreach (var role in new[] { StyleRoles.OrderedList, StyleRoles.UnorderedList })
            {
                if (!roleStyles.ContainsKey(role))
                {
                    roleStyles[role] = genericListStyle;
                    roleSelectors[role].AddRange(genericListSelectors);
                }
            }
        }

        return new ParsedStyleMap(
            roleStyles,
            roleSelectors.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal),
            references);
    }

    private static string NormalizeSelector(string selector) => Regex.Replace(selector.Trim(), @"\s+", " ").ToLowerInvariant();

    private static string? ResolveRole(string selector, out bool isGenericList)
    {
        isGenericList = false;
        if (Regex.IsMatch(selector, @"(^|\s|>)h1(?:[.#:\[]|$)")) return StyleRoles.Heading1;
        if (Regex.IsMatch(selector, @"(^|\s|>)h2(?:[.#:\[]|$)")) return StyleRoles.Heading2;
        if (Regex.IsMatch(selector, @"(^|\s|>)h3(?:[.#:\[]|$)")) return StyleRoles.Heading3;
        if (Regex.IsMatch(selector, @"(^|\s|>)h4(?:[.#:\[]|$)")) return StyleRoles.Heading4;
        if (Regex.IsMatch(selector, @"(^|\s|>)h5(?:[.#:\[]|$)")) return StyleRoles.Heading5;
        if (Regex.IsMatch(selector, @"(^|\s|>)h6(?:[.#:\[]|$)")) return StyleRoles.Heading6;
        if (selector.Contains("manual-code-block-paragraph", StringComparison.Ordinal)) return StyleRoles.CodeBlock;
        if (selector.Contains("manual-inline-code", StringComparison.Ordinal)
            || Regex.IsMatch(selector, @"^code(?:[.#:\[]|$)")) return StyleRoles.InlineCode;
        if (selector.Contains("manual-table-caption", StringComparison.Ordinal) || Regex.IsMatch(selector, @"(^|\s)table\s*>?\s*caption(?:[.#:\[]|$)")) return StyleRoles.TableCaption;
        if (selector.Contains("manual-figure-caption", StringComparison.Ordinal) || selector.Contains("figcaption", StringComparison.Ordinal)) return StyleRoles.Caption;
        if (selector.Contains("manual-figure-image-paragraph", StringComparison.Ordinal)) return StyleRoles.FigureImage;
        if (selector.Contains("manual-admonition-paragraph", StringComparison.Ordinal)) return StyleRoles.Admonition;
        if (selector.Contains("manual-table-paragraph", StringComparison.Ordinal)) return StyleRoles.Table;
        if (selector.Contains("manual-body-ordered", StringComparison.Ordinal) || Regex.IsMatch(selector, @"(^|\s)ol\s*>")) return StyleRoles.OrderedList;
        if (selector.Contains("manual-body-unordered", StringComparison.Ordinal) || Regex.IsMatch(selector, @"(^|\s)ul\s*>")) return StyleRoles.UnorderedList;
        if (selector.Contains("manual-body-item", StringComparison.Ordinal) || selector.Contains("manual-body-list-item", StringComparison.Ordinal))
        {
            isGenericList = true;
            return null;
        }
        if (selector.Contains("manual-body-paragraph", StringComparison.Ordinal)) return StyleRoles.Body;
        return null;
    }
}
