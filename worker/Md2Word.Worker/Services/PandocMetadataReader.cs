using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal sealed record VersionTableMetadata(
    string Bookmark,
    IReadOnlyList<string> ColumnKeys,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

internal sealed record HeadingNumberingLevelMetadata(string NumberStyle, string Format);

internal static class HeadingNumberingMetadataDefaults
{
    public static IReadOnlyDictionary<int, HeadingNumberingLevelMetadata> Levels { get; } =
        new ReadOnlyDictionary<int, HeadingNumberingLevelMetadata>(
            new Dictionary<int, HeadingNumberingLevelMetadata>
            {
                [1] = new("decimal", "%1."),
                [2] = new("decimal", "%1.%2"),
                [3] = new("decimal", "%1.%2.%3"),
                [4] = new("decimal", "%1.%2.%3.%4"),
            });

    public static IReadOnlySet<string> SupportedNumberStyles { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "decimal",
        "upper_roman",
        "lower_roman",
        "upper_letter",
        "lower_letter",
    };
}

internal sealed record PandocMetadata(
    string? Title,
    string? Subtitle,
    IReadOnlyList<VersionTableMetadata> VersionTables,
    bool FigureCaptions = true,
    bool HeadingNumbering = true,
    int HeadingNumberingStartBase = 1,
    IReadOnlyDictionary<int, HeadingNumberingLevelMetadata>? WordHeadingNumbering = null,
    bool WordRepeatTableHeaders = true)
{
    public bool HasExplicitValues => Title is not null || Subtitle is not null || VersionTables.Count > 0;

    public IReadOnlyDictionary<int, HeadingNumberingLevelMetadata> EffectiveWordHeadingNumbering =>
        WordHeadingNumbering ?? HeadingNumberingMetadataDefaults.Levels;
}

internal sealed class PandocMetadataReader
{
    private const string VersionTablesKey = "manul_version_tables";

    public async Task<PandocMetadata> ReadAsync(
        string pandocPath,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pandocPath))
        {
            throw new WorkerCommandException("PANDOC_MISSING", "Pandoc path is required.", 3, "metadata");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pandocPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { sourcePath, "--from=markdown", "--to=json" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new WorkerCommandException(
                    "PANDOC_METADATA_START_FAILED",
                    "Pandoc could not be started for metadata extraction.",
                    3,
                    "metadata");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new WorkerCommandException(
                "PANDOC_MISSING",
                "Pandoc could not be started for metadata extraction.",
                3,
                "metadata",
                inner: exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var json = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new WorkerCommandException(
                "PANDOC_METADATA_FAILED",
                $"Pandoc failed to read Markdown metadata (exit code {process.ExitCode}).",
                4,
                "metadata");
        }

        return ParseJson(json);
    }

    internal static PandocMetadata ParseJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("meta", out var meta)
                || meta.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("Pandoc JSON does not contain a metadata object.");
            }

            var title = ReadOptionalText(meta, "title");
            var subtitle = ReadOptionalText(meta, "subtitle");
            var versionTables = ReadVersionTables(meta);
            var figureCaptions = ReadOptionalBoolean(meta, "figure_captions", defaultValue: true);
            var headingNumbering = ReadOptionalBoolean(meta, "heading_numbering", defaultValue: true);
            var headingNumberingStartBase = ReadOptionalPositiveInteger(meta, "heading_numbering_start_base", defaultValue: 1);
            var wordHeadingNumbering = ReadWordHeadingNumbering(meta);
            var wordRepeatTableHeaders = ReadOptionalBoolean(meta, "word_repeat_table_headers", defaultValue: true);
            return new PandocMetadata(
                title,
                subtitle,
                versionTables,
                figureCaptions,
                headingNumbering,
                headingNumberingStartBase,
                wordHeadingNumbering,
                wordRepeatTableHeaders);
        }
        catch (WorkerCommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new WorkerCommandException(
                "FRONT_MATTER_INVALID",
                "Pandoc returned metadata JSON with an invalid shape.",
                4,
                "metadata",
                inner: exception);
        }
    }

    private static string? ReadOptionalText(JsonElement meta, string key)
    {
        if (!meta.TryGetProperty(key, out var value))
        {
            return null;
        }

        var text = ReadMetaText(value, key).Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool ReadOptionalBoolean(JsonElement meta, string key, bool defaultValue)
    {
        if (!meta.TryGetProperty(key, out var value))
        {
            return defaultValue;
        }
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("t", out var tag)
            && tag.ValueKind == JsonValueKind.String
            && string.Equals(tag.GetString(), "MetaBool", StringComparison.Ordinal)
            && value.TryGetProperty("c", out var content)
            && content.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return content.GetBoolean();
        }
        throw Invalid($"{key} must be a boolean metadata value.");
    }

    private static int ReadOptionalPositiveInteger(JsonElement meta, string key, int defaultValue)
    {
        if (!meta.TryGetProperty(key, out var value))
        {
            return defaultValue;
        }

        var text = ReadMetaText(value, key).Trim();
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 1)
        {
            throw Invalid($"{key} must be a positive integer metadata value.");
        }
        return result;
    }

    private static IReadOnlyDictionary<int, HeadingNumberingLevelMetadata> ReadWordHeadingNumbering(JsonElement meta)
    {
        if (!meta.TryGetProperty("word_heading_numbering", out var value))
        {
            return HeadingNumberingMetadataDefaults.Levels;
        }

        var map = RequireTaggedObject(value, "MetaMap", "word_heading_numbering");
        var levels = new Dictionary<int, HeadingNumberingLevelMetadata>();
        for (var level = 1; level <= 4; level++)
        {
            var key = $"level{level}";
            var defaults = HeadingNumberingMetadataDefaults.Levels[level];
            if (!map.TryGetProperty(key, out var levelValue))
            {
                levels[level] = defaults;
                continue;
            }

            var levelMap = RequireTaggedObject(levelValue, "MetaMap", $"word_heading_numbering.{key}");
            var numberStyle = levelMap.TryGetProperty("number_style", out var numberStyleValue)
                ? ReadMetaText(numberStyleValue, $"word_heading_numbering.{key}.number_style").Trim()
                : defaults.NumberStyle;
            var format = levelMap.TryGetProperty("format", out var formatValue)
                ? ReadMetaText(formatValue, $"word_heading_numbering.{key}.format").Trim()
                : defaults.Format;

            if (!HeadingNumberingMetadataDefaults.SupportedNumberStyles.Contains(numberStyle))
            {
                throw Invalid(
                    $"word_heading_numbering.{key}.number_style must be one of: "
                    + string.Join(", ", HeadingNumberingMetadataDefaults.SupportedNumberStyles.Order(StringComparer.Ordinal))
                    + ".");
            }
            if (format.Length == 0)
            {
                throw Invalid($"word_heading_numbering.{key}.format must not be empty.");
            }

            levels[level] = new HeadingNumberingLevelMetadata(numberStyle, format);
        }

        return new ReadOnlyDictionary<int, HeadingNumberingLevelMetadata>(levels);
    }

    private static IReadOnlyList<VersionTableMetadata> ReadVersionTables(JsonElement meta)
    {
        if (!meta.TryGetProperty(VersionTablesKey, out var value))
        {
            return Array.Empty<VersionTableMetadata>();
        }

        var entries = RequireTaggedArray(value, "MetaList", VersionTablesKey);
        if (entries.GetArrayLength() == 0)
        {
            return Array.Empty<VersionTableMetadata>();
        }

        var tables = new List<VersionTableMetadata>(entries.GetArrayLength());
        var bookmarks = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var path = $"{VersionTablesKey}[{index}]";
            var map = RequireTaggedObject(entry, "MetaMap", path);
            EnsureOnlyKeys(map, path, "bookmark", "column_keys", "rows");

            var bookmark = ReadRequiredText(map, "bookmark", path).Trim();
            if (bookmark.Length == 0)
            {
                throw Invalid($"{path}.bookmark must be non-empty.");
            }
            if (!bookmarks.Add(bookmark))
            {
                throw Invalid($"{VersionTablesKey} contains a duplicate bookmark.");
            }

            var columnValues = RequireTaggedArray(
                ReadRequiredProperty(map, "column_keys", path),
                "MetaList",
                $"{path}.column_keys");
            var columnKeys = new List<string>(columnValues.GetArrayLength());
            var uniqueColumns = new HashSet<string>(StringComparer.Ordinal);
            var columnIndex = 0;
            foreach (var columnValue in columnValues.EnumerateArray())
            {
                var key = ReadMetaText(columnValue, $"{path}.column_keys[{columnIndex}]").Trim();
                if (key.Length == 0 || !uniqueColumns.Add(key))
                {
                    throw Invalid($"{path}.column_keys must contain unique non-empty strings.");
                }
                columnKeys.Add(key);
                columnIndex++;
            }
            if (columnKeys.Count == 0)
            {
                throw Invalid($"{path}.column_keys must not be empty.");
            }

            var rowValues = RequireTaggedArray(
                ReadRequiredProperty(map, "rows", path),
                "MetaList",
                $"{path}.rows");
            var rows = new List<IReadOnlyDictionary<string, string>>(rowValues.GetArrayLength());
            var rowIndex = 0;
            foreach (var rowValue in rowValues.EnumerateArray())
            {
                var rowPath = $"{path}.rows[{rowIndex}]";
                var rowMap = RequireTaggedObject(rowValue, "MetaMap", rowPath);
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in rowMap.EnumerateObject())
                {
                    if (!uniqueColumns.Contains(property.Name))
                    {
                        throw Invalid($"{rowPath} contains a key not declared by column_keys.");
                    }
                    row[property.Name] = ReadMetaText(property.Value, $"{rowPath}.{property.Name}");
                }
                rows.Add(row);
                rowIndex++;
            }

            tables.Add(new VersionTableMetadata(bookmark, columnKeys, rows));
            index++;
        }
        return tables;
    }

    private static string ReadRequiredText(JsonElement map, string key, string path) =>
        ReadMetaText(ReadRequiredProperty(map, key, path), $"{path}.{key}");

    private static JsonElement ReadRequiredProperty(JsonElement map, string key, string path)
    {
        if (!map.TryGetProperty(key, out var value))
        {
            throw Invalid($"{path} is missing required property {key}.");
        }
        return value;
    }

    private static void EnsureOnlyKeys(JsonElement map, string path, params string[] allowedKeys)
    {
        var allowed = new HashSet<string>(allowedKeys, StringComparer.Ordinal);
        if (map.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
        {
            throw Invalid($"{path} contains an unsupported property.");
        }
    }

    private static JsonElement RequireTaggedArray(JsonElement value, string tag, string path)
    {
        var content = RequireTaggedContent(value, tag, path);
        if (content.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{path} must be a {tag} array.");
        }
        return content;
    }

    private static JsonElement RequireTaggedObject(JsonElement value, string tag, string path)
    {
        var content = RequireTaggedContent(value, tag, path);
        if (content.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{path} must be a {tag} object.");
        }
        return content;
    }

    private static JsonElement RequireTaggedContent(JsonElement value, string expectedTag, string path)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("t", out var tag)
            || tag.ValueKind != JsonValueKind.String
            || !string.Equals(tag.GetString(), expectedTag, StringComparison.Ordinal)
            || !value.TryGetProperty("c", out var content))
        {
            throw Invalid($"{path} must use Pandoc {expectedTag} metadata.");
        }
        return content;
    }

    private static string ReadMetaText(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("t", out var tagElement)
            || tagElement.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{path} must be textual Pandoc metadata.");
        }

        var tag = tagElement.GetString();
        if (!value.TryGetProperty("c", out var content))
        {
            throw Invalid($"{path} is missing Pandoc metadata content.");
        }
        return tag switch
        {
            "MetaString" when content.ValueKind == JsonValueKind.String => content.GetString() ?? string.Empty,
            "MetaInlines" when content.ValueKind == JsonValueKind.Array => ReadInlines(content, path),
            "MetaBlocks" when content.ValueKind == JsonValueKind.Array => ReadBlocks(content, path),
            _ => throw Invalid($"{path} must be textual Pandoc metadata."),
        };
    }

    private static string ReadInlines(JsonElement inlines, string path)
    {
        var result = new StringBuilder();
        foreach (var inline in inlines.EnumerateArray())
        {
            if (inline.ValueKind != JsonValueKind.Object
                || !inline.TryGetProperty("t", out var tagElement)
                || tagElement.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"{path} contains invalid Pandoc inline metadata.");
            }

            inline.TryGetProperty("c", out var content);
            switch (tagElement.GetString())
            {
                case "Str" when content.ValueKind == JsonValueKind.String:
                    result.Append(content.GetString());
                    break;
                case "Space":
                    result.Append(' ');
                    break;
                case "SoftBreak":
                case "LineBreak":
                    result.Append('\n');
                    break;
                case "Code" when content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 2:
                case "Math" when content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 2:
                    result.Append(content[1].GetString());
                    break;
                case "Emph":
                case "Underline":
                case "Strong":
                case "Strikeout":
                case "Superscript":
                case "Subscript":
                case "SmallCaps":
                    result.Append(ReadInlineArray(content, path));
                    break;
                case "Quoted" when content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 2:
                case "Cite" when content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 2:
                    result.Append(ReadInlineArray(content[1], path));
                    break;
                case "Span" when content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 2:
                case "Link" when content.ValueKind == JsonValueKind.Array && content.GetArrayLength() >= 2:
                case "Image" when content.ValueKind == JsonValueKind.Array && content.GetArrayLength() >= 2:
                    result.Append(ReadInlineArray(content[1], path));
                    break;
                default:
                    throw Invalid($"{path} contains unsupported Pandoc inline metadata.");
            }
        }
        return result.ToString();
    }

    private static string ReadInlineArray(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{path} contains invalid Pandoc inline metadata.");
        }
        return ReadInlines(value, path);
    }

    private static string ReadBlocks(JsonElement blocks, string path)
    {
        var values = new List<string>();
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("t", out var tagElement)
                || tagElement.ValueKind != JsonValueKind.String
                || !block.TryGetProperty("c", out var content))
            {
                throw Invalid($"{path} contains invalid Pandoc block metadata.");
            }
            values.Add(tagElement.GetString() switch
            {
                "Plain" or "Para" => ReadInlineArray(content, path),
                "Header" when content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 3 =>
                    ReadInlineArray(content[2], path),
                "CodeBlock" when content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 2 =>
                    content[1].GetString() ?? string.Empty,
                "BlockQuote" => ReadBlocks(content, path),
                "Div" when content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 2 =>
                    ReadBlocks(content[1], path),
                _ => throw Invalid($"{path} contains unsupported Pandoc block metadata."),
            });
        }
        return string.Join("\n", values);
    }

    private static WorkerCommandException Invalid(string message) =>
        new("FRONT_MATTER_INVALID", message, 4, "metadata");

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process exited between the checks.
        }
    }
}
