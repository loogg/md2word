using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal sealed record ResolvedStyle(string StyleId, string Name, int? WordBuiltInStyleId = null);

internal sealed record TemplateValidationOutcome(
    TemplateValidationReport Report,
    ParsedStyleMap ParsedStyleMap,
    IReadOnlyDictionary<string, ResolvedStyle> ResolvedRoleStyles);

internal sealed class TemplateValidationService
{
    public const string BodyStartBookmark = "MANUAL_BODY_START";
    public const string BodyEndBookmark = "MANUAL_BODY_END";
    public const string CoverTitleBookmark = "MANUAL_COVER_TITLE";
    public const string CoverSubtitleBookmark = "MANUAL_COVER_SUBTITLE";

    public TemplateValidationOutcome Validate(string docxPath, string cssPath)
    {
        var issues = new List<ValidationIssue>();
        var parsedStyleMap = new ParsedStyleMap(
            new Dictionary<string, string>(),
            StyleRoles.All.ToDictionary(role => role, _ => (IReadOnlyList<string>)Array.Empty<string>()),
            Array.Empty<CssStyleReference>());
        var roleReports = new List<StyleMappingReport>();
        var capabilities = new TemplateCapabilities(false, false, false, [], false);
        var fingerprint = string.Empty;
        var resolvedRoleStyles = new Dictionary<string, ResolvedStyle>(StringComparer.Ordinal);

        if (!IsReadableFile(docxPath) || !string.Equals(Path.GetExtension(docxPath), ".docx", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error("TEMPLATE_FILE_UNREADABLE", "docx", "The template DOCX does not exist or is not readable."));
        }
        if (!IsReadableFile(cssPath) || !string.Equals(Path.GetExtension(cssPath), ".css", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error("CSS_FILE_UNREADABLE", "css", "The template CSS does not exist or is not readable."));
        }

        if (issues.Count == 0)
        {
            try
            {
                fingerprint = ComputeFingerprint(docxPath, cssPath);
                parsedStyleMap = StyleMapParser.ParseFile(cssPath);
                using var document = WordprocessingDocument.Open(docxPath, false);
                var mainPart = document.MainDocumentPart
                    ?? throw new InvalidDataException("The package has no main document part.");
                var body = mainPart.Document?.Body
                    ?? throw new InvalidDataException("The package has no document body.");
                var styleIndex = StyleIndex.Create(mainPart.StyleDefinitionsPart?.Styles);
                if (ContainsUnsafeExternalContent(mainPart, body))
                {
                    issues.Add(Error(
                        "TEMPLATE_EXTERNAL_CONTENT_BLOCKED",
                        "security",
                        "The template contains an external relationship or field that could load network or local content automatically."));
                }

                var bookmarkNames = body.Descendants<BookmarkStart>()
                    .Select(bookmark => bookmark.Name?.Value)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToArray();
                var bodyStartIndex = Array.IndexOf(bookmarkNames, BodyStartBookmark);
                var bodyEndIndex = Array.IndexOf(bookmarkNames, BodyEndBookmark);
                var bodyRange = bodyStartIndex >= 0 && bodyEndIndex >= 0 && bodyStartIndex < bodyEndIndex;
                if (bodyStartIndex < 0 || bodyEndIndex < 0)
                {
                    issues.Add(Error("BODY_BOOKMARK_MISSING", "bookmark", "The template must contain MANUAL_BODY_START and MANUAL_BODY_END."));
                }
                else if (bodyStartIndex >= bodyEndIndex)
                {
                    issues.Add(Error("BODY_BOOKMARK_ORDER_INVALID", "bookmark", "MANUAL_BODY_END must appear after MANUAL_BODY_START."));
                }

                var coverTitle = bookmarkNames.Contains(CoverTitleBookmark, StringComparer.Ordinal);
                var coverSubtitle = bookmarkNames.Contains(CoverSubtitleBookmark, StringComparer.Ordinal);
                if (!coverTitle || !coverSubtitle)
                {
                    issues.Add(Warning(
                        "OPTIONAL_BOOKMARK_MISSING",
                        "bookmark",
                        "One or more optional cover bookmarks are unavailable; conversion remains possible when the corresponding metadata is not used."));
                }

                var missingReferencedStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ambiguousReferencedStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var reference in parsedStyleMap.References)
                {
                    var matches = styleIndex.Resolve(reference.StyleName);
                    if (matches.Count == 0 && missingReferencedStyles.Add(reference.StyleName))
                    {
                        issues.Add(Error("WORD_STYLE_MISSING", "style", $"CSS references a Word paragraph style that the template cannot resolve: {reference.StyleName}."));
                    }
                    else if (matches.Count > 1 && ambiguousReferencedStyles.Add(reference.StyleName))
                    {
                        issues.Add(Error("WORD_STYLE_AMBIGUOUS", "style", $"CSS references an ambiguous Word paragraph style: {reference.StyleName}."));
                    }
                }

                roleReports = [];
                foreach (var role in StyleRoles.All)
                {
                    var publicRole = ToPublicRole(role);
                    parsedStyleMap.RoleStyles.TryGetValue(role, out var requestedStyleName);
                    var selectors = parsedStyleMap.RoleSelectors.TryGetValue(role, out var configuredSelectors)
                        ? configuredSelectors
                        : Array.Empty<string>();
                    var cssSelector = string.Join(", ", selectors);
                    if (requestedStyleName is null)
                    {
                        // Inline code is a run-level semantic, but this Worker
                        // intentionally maps it through an existing Word
                        // paragraph style. If CSS does not declare a separate
                        // inline-code target, inherit the already-resolved body
                        // mapping. A custom body style therefore also controls
                        // inline code instead of generic Normal/HTML Code.
                        var fallback = role == StyleRoles.InlineCode
                            && resolvedRoleStyles.TryGetValue(StyleRoles.Body, out var bodyStyle)
                            ? new NativeFallbackResolution(bodyStyle, null)
                            : ResolveWordNativeFallback(styleIndex, role);
                        if (fallback.AmbiguousCandidate is not null)
                        {
                            issues.Add(Error(
                                "WORD_NATIVE_STYLE_FALLBACK_AMBIGUOUS",
                                "style",
                                $"The template has more than one Word paragraph style matching the native {role} fallback: {fallback.AmbiguousCandidate}."));
                        }
                        else if (fallback.Style is not null)
                        {
                            resolvedRoleStyles[role] = fallback.Style;
                        }

                        if (publicRole is not null)
                        {
                            roleReports.Add(new StyleMappingReport(
                                publicRole.Value.Role,
                                publicRole.Value.HeadingLevel,
                                cssSelector,
                                string.Empty,
                                fallback.Style?.StyleId,
                                fallback.Style?.Name,
                                fallback.Style is not null ? "word-fallback" : "not-configured"));
                        }
                        if (fallback.Style is null && fallback.AmbiguousCandidate is null)
                        {
                            var message = $"CSS does not map {role}, and the template has no usable Word-native paragraph style fallback.";
                            issues.Add(StyleRoles.RequiresSemanticWordFallback(role)
                                ? Warning(
                                    "WORD_NATIVE_STYLE_FALLBACK_MISSING",
                                    "style",
                                    $"{message} The template remains usable for source documents that do not produce {role} after heading remapping; conversion is blocked if that level is actually used.")
                                : Error("WORD_NATIVE_STYLE_FALLBACK_MISSING", "style", message));
                        }
                        continue;
                    }

                    var matches = styleIndex.Resolve(requestedStyleName);
                    if (matches.Count == 1)
                    {
                        var resolved = AttachWordBuiltInStyleId(role, matches[0], requestedStyleName);
                        resolvedRoleStyles[role] = resolved;
                        if (publicRole is not null)
                        {
                            roleReports.Add(new StyleMappingReport(
                                publicRole.Value.Role,
                                publicRole.Value.HeadingLevel,
                                cssSelector,
                                requestedStyleName,
                                resolved.StyleId,
                                resolved.Name,
                                "resolved"));
                        }
                    }
                    else if (publicRole is not null)
                    {
                        roleReports.Add(new StyleMappingReport(
                            publicRole.Value.Role,
                            publicRole.Value.HeadingLevel,
                            cssSelector,
                            requestedStyleName,
                            null,
                            null,
                            matches.Count == 0 ? "missing" : "ambiguous"));
                    }
                }

                var versionTables = bookmarkNames
                    .Where(name => name.StartsWith("MANUAL_TABLE_", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                capabilities = new TemplateCapabilities(
                    bodyRange,
                    coverTitle,
                    coverSubtitle,
                    versionTables,
                    resolvedRoleStyles.ContainsKey(StyleRoles.CodeBlock));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OpenXmlPackageException)
            {
                issues.Add(Error("TEMPLATE_FILE_UNREADABLE", "docx", "The template is not a readable Open XML DOCX package."));
            }
        }

        var status = issues.Any(issue => issue.Severity == "error")
            ? "invalid"
            : issues.Any(issue => issue.Severity == "warning") ? "warning" : "valid";
        var summary = status switch
        {
            "valid" => "Template bookmarks, CSS mappings, and Word-native style fallbacks are valid.",
            "warning" => "Template is usable with conditional or optional capability warnings.",
            _ => "Template or CSS validation failed.",
        };
        var report = new TemplateValidationReport(
            status,
            DateTimeOffset.UtcNow.ToString("O"),
            fingerprint,
            summary,
            issues,
            capabilities,
            roleReports);
        return new TemplateValidationOutcome(report, parsedStyleMap, resolvedRoleStyles);
    }

    public static void EnsureGeneratedDocumentIsOfflineSafe(string docxPath)
    {
        using var document = WordprocessingDocument.Open(docxPath, false);
        var mainPart = document.MainDocumentPart
            ?? throw new WorkerCommandException("OUTPUT_DOCX_INVALID", "The generated DOCX has no main document part.", 4, "openxml-finalize");
        var body = mainPart.Document?.Body
            ?? throw new WorkerCommandException("OUTPUT_DOCX_INVALID", "The generated DOCX has no document body.", 4, "openxml-finalize");
        var unsafeContentReason = FindUnsafeExternalContentReason(mainPart, body);
        if (unsafeContentReason is not null)
        {
            throw new WorkerCommandException(
                "OUTPUT_EXTERNAL_CONTENT_BLOCKED",
                $"The generated DOCX contains externally loaded content and was not published ({unsafeContentReason}).",
                4,
                "openxml-finalize");
        }
    }

    private static (string Role, int? HeadingLevel)? ToPublicRole(string role) => role switch
    {
        StyleRoles.Body => ("body", null),
        StyleRoles.OrderedList => ("ordered-list", null),
        StyleRoles.UnorderedList => ("unordered-list", null),
        StyleRoles.Heading1 => ("heading", 1),
        StyleRoles.Heading2 => ("heading", 2),
        StyleRoles.Heading3 => ("heading", 3),
        StyleRoles.Heading4 => ("heading", 4),
        StyleRoles.Heading5 => ("heading", 5),
        StyleRoles.Heading6 => ("heading", 6),
        StyleRoles.Caption => ("caption", null),
        StyleRoles.TableCaption => ("table-caption", null),
        StyleRoles.CodeBlock => ("code-block", null),
        StyleRoles.InlineCode => ("inline-code", null),
        StyleRoles.Table => ("table", null),
        StyleRoles.Admonition => ("admonition", null),
        StyleRoles.FigureImage => ("figure-image", null),
        _ => null,
    };

    private static NativeFallbackResolution ResolveWordNativeFallback(StyleIndex styleIndex, string role)
    {
        foreach (var candidate in StyleRoles.WordNativeFallbackNames(role))
        {
            var matches = styleIndex.Resolve(candidate);
            if (matches.Count > 1)
            {
                return new NativeFallbackResolution(null, candidate);
            }
            if (matches.Count == 1)
            {
                return new NativeFallbackResolution(AttachWordBuiltInStyleId(role, matches[0], candidate), null);
            }
        }

        return StyleRoles.RequiresSemanticWordFallback(role)
            ? new NativeFallbackResolution(null, null)
            : new NativeFallbackResolution(styleIndex.DefaultParagraphStyle, null);
    }

    private static ResolvedStyle AttachWordBuiltInStyleId(string role, ResolvedStyle style, string matchedName)
    {
        if (!StyleRoles.WordNativeFallbackNames(role).Contains(matchedName, StringComparer.OrdinalIgnoreCase))
        {
            return style;
        }
        var builtInStyleId = role switch
        {
            StyleRoles.Heading1 => -2,
            StyleRoles.Heading2 => -3,
            StyleRoles.Heading3 => -4,
            StyleRoles.Heading4 => -5,
            StyleRoles.Heading5 => -6,
            StyleRoles.Heading6 => -7,
            _ => (int?)null,
        };
        return builtInStyleId is null ? style : style with { WordBuiltInStyleId = builtInStyleId };
    }

    private static ValidationIssue Error(string code, string target, string message) =>
        new(code, "error", target, message, CapabilityIdForIssue(code));

    private static ValidationIssue Warning(string code, string target, string message) =>
        new(code, "warning", target, message, CapabilityIdForIssue(code));

    private static string? CapabilityIdForIssue(string code) => code switch
    {
        "TEMPLATE_FILE_UNREADABLE" => "CAP-TEMPLATE-DOCX-PACKAGE",
        "CSS_FILE_UNREADABLE" or "WORD_STYLE_MISSING" or "WORD_STYLE_AMBIGUOUS" =>
            "CAP-TEMPLATE-CSS-MAPPING",
        "BODY_BOOKMARK_MISSING" or "BODY_BOOKMARK_ORDER_INVALID" =>
            "CAP-TEMPLATE-BODY-RANGE",
        "OPTIONAL_BOOKMARK_MISSING" =>
            "CAP-TEMPLATE-OPTIONAL-BOOKMARKS",
        "WORD_NATIVE_STYLE_FALLBACK_MISSING" or "WORD_NATIVE_STYLE_FALLBACK_AMBIGUOUS" =>
            "CAP-TEMPLATE-WORD-STYLE-FALLBACK",
        "TEMPLATE_EXTERNAL_CONTENT_BLOCKED" =>
            "CAP-TEMPLATE-OFFLINE-SAFETY",
        _ => null,
    };

    private sealed record NativeFallbackResolution(ResolvedStyle? Style, string? AmbiguousCandidate);

    private static bool ContainsUnsafeExternalContent(MainDocumentPart mainPart, Body body) =>
        FindUnsafeExternalContentReason(mainPart, body) is not null;

    private static string? FindUnsafeExternalContentReason(MainDocumentPart mainPart, Body body)
    {
        var externalRelationship = new[] { (OpenXmlPart)mainPart }.Concat(EnumerateParts(mainPart))
            .SelectMany(part => part.ExternalRelationships)
            .FirstOrDefault(relationship => !relationship.RelationshipType.EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase));
        if (externalRelationship is not null)
        {
            return $"relationship type: {externalRelationship.RelationshipType}";
        }

        var roots = new List<OpenXmlElement> { body };
        roots.AddRange(mainPart.HeaderParts.Select(part => part.Header).OfType<OpenXmlElement>());
        roots.AddRange(mainPart.FooterParts.Select(part => part.Footer).OfType<OpenXmlElement>());
        if (mainPart.FootnotesPart?.Footnotes is { } footnotes) roots.Add(footnotes);
        if (mainPart.EndnotesPart?.Endnotes is { } endnotes) roots.Add(endnotes);
        if (mainPart.WordprocessingCommentsPart?.Comments is { } comments) roots.Add(comments);

        var instructions = roots
            .SelectMany(ExtractComplexFieldInstructions)
            .Concat(roots.SelectMany(root => root.Descendants<SimpleField>()).Select(field => field.Instruction?.Value ?? string.Empty));
        return instructions.Any(instruction => Regex.IsMatch(
            instruction,
            @"(?:^|\s)(?:DDEAUTO|DDE|INCLUDEPICTURE|INCLUDETEXT|LINK|RD|DATABASE)\b",
            RegexOptions.IgnoreCase))
            ? "unsafe field instruction"
            : null;
    }

    private static IEnumerable<string> ExtractComplexFieldInstructions(OpenXmlElement root)
    {
        var instructions = new List<string>();
        var stack = new List<FieldInstructionState>();
        foreach (var element in root.Descendants())
        {
            if (element is FieldChar fieldChar)
            {
                var type = fieldChar.FieldCharType?.Value;
                if (type == FieldCharValues.Begin)
                {
                    stack.Add(new FieldInstructionState());
                }
                else if (type == FieldCharValues.Separate && stack.Count > 0)
                {
                    stack[^1].Collecting = false;
                }
                else if (type == FieldCharValues.End && stack.Count > 0)
                {
                    instructions.Add(stack[^1].Text.ToString());
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            else if (element is FieldCode code)
            {
                foreach (var state in stack.Where(state => state.Collecting)) state.Text.Append(code.Text);
            }
        }
        instructions.AddRange(stack.Select(state => state.Text.ToString()));
        return instructions;
    }

    private sealed class FieldInstructionState
    {
        public StringBuilder Text { get; } = new();
        public bool Collecting { get; set; } = true;
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

    private static bool IsReadableFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length >= 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string ComputeFingerprint(string docxPath, string cssPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFile(hash, docxPath);
        hash.AppendData([0]);
        AppendFile(hash, cssPath);
        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static void AppendFile(IncrementalHash hash, string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
        }
    }

    private sealed class StyleIndex
    {
        private readonly Dictionary<string, List<ResolvedStyle>> byKey = new(StringComparer.OrdinalIgnoreCase);

        public ResolvedStyle? DefaultParagraphStyle { get; private set; }

        public static StyleIndex Create(Styles? styles)
        {
            var index = new StyleIndex();
            if (styles is null)
            {
                return index;
            }
            foreach (var style in styles.Elements<Style>())
            {
                if (style.Type?.Value != StyleValues.Paragraph || string.IsNullOrWhiteSpace(style.StyleId?.Value))
                {
                    continue;
                }
                var styleId = style.StyleId!.Value!;
                var name = style.StyleName?.Val?.Value ?? styleId;
                var resolved = new ResolvedStyle(styleId, name);
                if (style.Default?.Value == true && index.DefaultParagraphStyle is null)
                {
                    index.DefaultParagraphStyle = resolved;
                }
                index.Add(styleId, resolved);
                index.Add(name, resolved);
                var aliases = style.Aliases?.Val?.Value;
                if (!string.IsNullOrWhiteSpace(aliases))
                {
                    foreach (var alias in aliases.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        index.Add(alias, resolved);
                    }
                }
            }
            return index;
        }

        public IReadOnlyList<ResolvedStyle> Resolve(string key)
        {
            var normalized = key.Trim();
            if (byKey.TryGetValue(normalized, out var values))
            {
                return values.DistinctBy(value => value.StyleId, StringComparer.Ordinal).ToArray();
            }

            return WordStyleNameEquivalents.Alternatives(normalized)
                .Where(byKey.ContainsKey)
                .SelectMany(alternative => byKey[alternative])
                .DistinctBy(value => value.StyleId, StringComparer.Ordinal)
                .ToArray();
        }

        private void Add(string? key, ResolvedStyle style)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }
            var normalized = key.Trim();
            if (!byKey.TryGetValue(normalized, out var values))
            {
                values = [];
                byKey[normalized] = values;
            }
            values.Add(style);
        }
    }
}
