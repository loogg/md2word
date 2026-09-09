using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal static class OpenXmlResolvedStyleRebinder
{
    public static IReadOnlyDictionary<string, ResolvedStyle> Rebind(
        string docxPath,
        IReadOnlyDictionary<string, ResolvedStyle> resolvedRoleStyles)
    {
        using var document = WordprocessingDocument.Open(docxPath, false);
        var styles = document.MainDocumentPart?.StyleDefinitionsPart?.Styles
            ?? throw new WorkerCommandException(
                "WORD_STYLE_REBIND_FAILED",
                "The Word-saved document has no paragraph style definitions.",
                4,
                "openxml-finalize");
        var index = BuildIndex(styles);
        var rebound = new Dictionary<string, ResolvedStyle>(StringComparer.Ordinal);

        foreach (var (role, original) in resolvedRoleStyles)
        {
            var matches = Resolve(index, original).ToArray();
            if (matches.Length == 0)
            {
                throw new WorkerCommandException(
                    "WORD_STYLE_REBIND_FAILED",
                    $"Microsoft Word changed or removed the resolved paragraph style for {role}: {original.Name}.",
                    4,
                    "openxml-finalize");
            }
            if (matches.Length > 1)
            {
                throw new WorkerCommandException(
                    "WORD_STYLE_REBIND_AMBIGUOUS",
                    $"Microsoft Word produced more than one paragraph style matching {role}: {original.Name}.",
                    4,
                    "openxml-finalize");
            }

            rebound[role] = matches[0] with { WordBuiltInStyleId = original.WordBuiltInStyleId };
        }

        return rebound;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ResolvedStyle>> BuildIndex(Styles styles)
    {
        var index = new Dictionary<string, List<ResolvedStyle>>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in styles.Elements<Style>())
        {
            if (style.Type?.Value != StyleValues.Paragraph || string.IsNullOrWhiteSpace(style.StyleId?.Value))
            {
                continue;
            }

            var styleId = style.StyleId!.Value!;
            var name = style.StyleName?.Val?.Value ?? styleId;
            var resolved = new ResolvedStyle(styleId, name);
            Add(index, styleId, resolved);
            Add(index, name, resolved);
            var aliases = style.Aliases?.Val?.Value;
            if (!string.IsNullOrWhiteSpace(aliases))
            {
                foreach (var alias in aliases.Split(
                             new[] { ',', ';' },
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Add(index, alias, resolved);
                }
            }
        }

        return index.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ResolvedStyle>)pair.Value
                .DistinctBy(style => style.StyleId, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<ResolvedStyle> Resolve(
        IReadOnlyDictionary<string, IReadOnlyList<ResolvedStyle>> index,
        ResolvedStyle original)
    {
        var directById = Lookup(index, original.StyleId);
        if (directById.Count > 0)
        {
            return directById;
        }

        var directByName = Lookup(index, original.Name);
        if (directByName.Count > 0)
        {
            return directByName;
        }

        return WordStyleNameEquivalents.Alternatives(original.Name)
            .SelectMany(name => Lookup(index, name))
            .DistinctBy(style => style.StyleId, StringComparer.Ordinal);
    }

    private static IReadOnlyList<ResolvedStyle> Lookup(
        IReadOnlyDictionary<string, IReadOnlyList<ResolvedStyle>> index,
        string key) => index.TryGetValue(key.Trim(), out var matches) ? matches : [];

    private static void Add(
        IDictionary<string, List<ResolvedStyle>> index,
        string? key,
        ResolvedStyle style)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        if (!index.TryGetValue(key.Trim(), out var matches))
        {
            matches = [];
            index[key.Trim()] = matches;
        }
        matches.Add(style);
    }
}
