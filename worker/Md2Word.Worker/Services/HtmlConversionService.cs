using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Md2Word.Worker.Protocol;
using System.Text;
using System.Text.RegularExpressions;

namespace Md2Word.Worker.Services;

internal sealed class HtmlConversionService
{
    internal const string ImportBoundaryRole = "import-boundary";
    internal const string InternalBookmarkPrefix = "md2word_bm_";
    internal const string InternalLinkMarkerAttribute = "data-md2word-internal-link";
    internal const string AdmonitionVisualGenericRole = "admonition-visual-generic";
    internal const string AdmonitionVisualNoteRole = "admonition-visual-note";
    internal const string AdmonitionVisualCautionRole = "admonition-visual-caution";
    internal const string AdmonitionVisualWarningRole = "admonition-visual-warning";
    internal const string AdmonitionVisualDangerRole = "admonition-visual-danger";

    public async Task<string> TransformAsync(
        string html,
        string css,
        ParsedStyleMap styleMap,
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        RejectUnsafeActiveContent(document, css);

        foreach (var preformatted in document.QuerySelectorAll("pre").ToArray())
        {
            var paragraph = document.CreateElement("p");
            paragraph.ClassList.Add("manual-code-block-paragraph");
            AppendCodeWithExplicitLineBreaks(document, paragraph, preformatted.TextContent);
            var sourceContainer = preformatted.ParentElement is { } parent && parent.ClassList.Contains("sourceCode")
                ? parent
                : preformatted;
            sourceContainer.Replace(paragraph);
        }

        MarkInlineCode(document);

        NormalizeCaptionedFigures(document);
        NormalizeTableCaptions(document);
        NormalizeTableCellParagraphs(document);

        foreach (var list in document.QuerySelectorAll("ol, ul"))
        {
            list.ClassList.Add("manual-body-list");
            list.ClassList.Add(list.LocalName == "ol" ? "manual-body-ordered-list" : "manual-body-unordered-list");
        }

        foreach (var item in document.QuerySelectorAll("li"))
        {
            item.ClassList.Add("manual-body-item");
            item.ClassList.Add("manual-body-list-item");
            var list = NearestList(item);
            if (list?.LocalName == "ol")
            {
                item.ClassList.Add("manual-body-ordered-item");
            }
            else if (list?.LocalName == "ul")
            {
                item.ClassList.Add("manual-body-unordered-item");
            }
        }

        foreach (var caption in document.QuerySelectorAll("figcaption"))
        {
            caption.ClassList.Add("manual-figure-caption");
        }

        foreach (var paragraph in document.QuerySelectorAll("p"))
        {
            if (paragraph.ClassList.Contains("manual-code-block-paragraph"))
            {
                continue;
            }
            if (paragraph.ClassList.Contains("manual-figure-image-paragraph")
                && NearestAncestor(paragraph, "li") is null)
            {
                // A top-level figure image needs its own centered paragraph, but
                // it must not inherit the body style. Applying the body style here
                // would discard the explicit centering when Open XML roles are
                // finalized. List-contained figures still carry list context.
                continue;
            }
            var item = NearestAncestor(paragraph, "li");
            if (item is not null)
            {
                paragraph.ClassList.Add("manual-body-item-paragraph");
                var list = NearestList(item);
                if (list?.LocalName == "ol")
                {
                    paragraph.ClassList.Add("manual-body-ordered-item-paragraph");
                }
                else if (list?.LocalName == "ul")
                {
                    paragraph.ClassList.Add("manual-body-unordered-item-paragraph");
                }
            }
            else if (NearestAncestor(paragraph, "table") is not null)
            {
                paragraph.ClassList.Add("manual-table-paragraph");
            }
            else if (NearestAncestorWithClass(paragraph, "admonition") is not null)
            {
                paragraph.ClassList.Add("manual-admonition-paragraph");
            }
            else
            {
                paragraph.ClassList.Add("manual-body-paragraph");
            }
        }

        NormalizeInternalAnchorBookmarks(document);
        AddRoleMarkers(document);
        AddImportBoundary(document);

        RewriteLocalReferences(document, sourceDirectory);
        var style = document.CreateElement("style");
        style.TextContent = css + Environment.NewLine + styleMap.BuildCompatibilityCss();
        IElement? head = document.Head;
        if (head is null)
        {
            head = document.CreateElement("head");
            document.DocumentElement.AppendChild(head);
        }
        head.AppendChild(style);
        return document.DocumentElement.OuterHtml;
    }

    private static void AppendCodeWithExplicitLineBreaks(
        IDocument document,
        IElement paragraph,
        string code)
    {
        var normalized = code
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                paragraph.AppendChild(document.CreateElement("br"));
            }
            if (lines[index].Length > 0)
            {
                paragraph.AppendChild(document.CreateTextNode(PreserveCodeSpacing(lines[index])));
            }
        }
    }

    private static string PreserveCodeSpacing(string line)
    {
        const char nonBreakingSpace = '\u00A0';
        var result = new StringBuilder(line.Length);
        var hasNonWhitespaceContent = false;

        for (var index = 0; index < line.Length;)
        {
            if (line[index] == '\t')
            {
                // Word's HTML importer does not reliably preserve tab characters
                // inside an ordinary paragraph. Use one predictable four-space tab.
                result.Append(nonBreakingSpace, 4);
                index++;
                continue;
            }

            if (line[index] != ' ')
            {
                result.Append(line[index]);
                hasNonWhitespaceContent = true;
                index++;
                continue;
            }

            var start = index;
            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }

            var count = index - start;
            var isTrailing = index == line.Length;
            if (!hasNonWhitespaceContent || isTrailing)
            {
                result.Append(nonBreakingSpace, count);
                continue;
            }

            if (count > 1)
            {
                result.Append(nonBreakingSpace, count - 1);
            }
            result.Append(' ');
        }

        return result.ToString();
    }

    private static void MarkInlineCode(IDocument document)
    {
        // Fenced code has already been converted to a plain paragraph above,
        // so every remaining <code> element is inline code. Boundary markers
        // survive Word's HTML import even when it invents an HTML Code
        // character style; the Open XML finalizer then removes that style and
        // projects the CSS-selected Word paragraph style onto just this range.
        foreach (var code in document.QuerySelectorAll("code").ToArray())
        {
            code.ClassList.Add("manual-inline-code");
            var markerId = Guid.NewGuid().ToString("N");
            var start = document.CreateElement("span");
            start.ClassList.Add("md2word-inline-code-boundary");
            start.TextContent = $"__MD2WORD_INLINE_CODE_START_{markerId}__";
            var end = document.CreateElement("span");
            end.ClassList.Add("md2word-inline-code-boundary");
            end.TextContent = $"__MD2WORD_INLINE_CODE_END_{markerId}__";
            code.Prepend(start);
            code.Append(end);
        }
    }

    private static void NormalizeCaptionedFigures(IDocument document)
    {
        foreach (var figure in document.QuerySelectorAll("figure").ToArray())
        {
            var caption = figure.Children.FirstOrDefault(child => child.LocalName == "figcaption");
            if (caption is null)
            {
                continue;
            }

            var imageNodes = figure.ChildNodes
                .Where(node => !ReferenceEquals(node, caption) && !IsWhitespaceText(node))
                .ToArray();
            var imageParagraph = ReuseOrCreateParagraph(document, imageNodes);
            imageParagraph.ClassList.Add("manual-figure-image-paragraph");
            imageParagraph.SetAttribute("style", "text-align: center; margin: 0;");

            var captionNodes = caption.ChildNodes
                .Where(node => !IsWhitespaceText(node))
                .ToArray();
            var captionParagraph = ReuseOrCreateParagraph(document, captionNodes);
            captionParagraph.ClassList.Add("manual-figure-caption");

            figure.Before(imageParagraph);
            figure.Before(captionParagraph);
            figure.Remove();
        }
    }

    private static void NormalizeTableCaptions(IDocument document)
    {
        foreach (var caption in document.QuerySelectorAll("table > caption").ToArray())
        {
            var nodes = caption.ChildNodes
                .Where(node => !IsWhitespaceText(node))
                .ToArray();
            if (nodes.Length == 0)
            {
                caption.Remove();
                continue;
            }

            var paragraph = ReuseOrCreateParagraph(document, nodes);
            paragraph.ClassList.Add("manual-table-caption");
            var table = caption.ParentElement;
            if (table is null)
            {
                continue;
            }
            table.Before(paragraph);
            caption.Remove();
        }
    }

    private static void NormalizeTableCellParagraphs(IDocument document)
    {
        foreach (var cell in document.QuerySelectorAll("th, td"))
        {
            var inlineGroup = new List<INode>();
            foreach (var node in cell.ChildNodes.ToArray())
            {
                if (IsHtmlBlock(node))
                {
                    WrapTableCellInlineGroup(document, cell, inlineGroup);
                    inlineGroup.Clear();
                    continue;
                }
                inlineGroup.Add(node);
            }
            WrapTableCellInlineGroup(document, cell, inlineGroup);
        }
    }

    private static bool IsHtmlBlock(INode node) => node is IElement element && element.LocalName is
        "address" or "article" or "aside" or "blockquote" or "div" or "dl" or "fieldset" or "figure"
        or "footer" or "form" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "header" or "hr"
        or "main" or "nav" or "ol" or "p" or "pre" or "section" or "table" or "ul";

    private static void WrapTableCellInlineGroup(
        IDocument document,
        IElement cell,
        IReadOnlyCollection<INode> nodes)
    {
        if (nodes.Count == 0 || nodes.All(IsWhitespaceText))
        {
            return;
        }

        var paragraph = document.CreateElement("p");
        cell.InsertBefore(paragraph, nodes.First());
        foreach (var node in nodes)
        {
            paragraph.AppendChild(node);
        }
    }

    private static IElement ReuseOrCreateParagraph(IDocument document, IReadOnlyList<INode> nodes)
    {
        if (nodes.Count == 1 && nodes[0] is IElement { LocalName: "p" } paragraph)
        {
            return paragraph;
        }

        var created = document.CreateElement("p");
        foreach (var node in nodes)
        {
            created.AppendChild(node);
        }
        return created;
    }

    private static bool IsWhitespaceText(INode node) =>
        node is IText text && string.IsNullOrWhiteSpace(text.Data);

    private static void AddImportBoundary(IDocument document)
    {
        IElement? body = document.Body;
        if (body is null)
        {
            body = document.CreateElement("body");
            document.DocumentElement.AppendChild(body);
        }
        var boundary = document.CreateElement("p");
        boundary.ClassList.Add("md2word-import-boundary");
        AddRoleMarker(document, boundary, ImportBoundaryRole);
        body.AppendChild(boundary);
    }

    private static void AddRoleMarkers(IDocument document)
    {
        foreach (var heading in document.QuerySelectorAll("h1, h2, h3, h4, h5, h6"))
        {
            AddRoleMarker(document, heading, heading.LocalName);
        }

        foreach (var paragraph in document.QuerySelectorAll("p"))
        {
            var role = paragraph.ClassList.Contains("manual-code-block-paragraph")
                ? StyleRoles.CodeBlock
                : paragraph.ClassList.Contains("manual-figure-caption")
                    ? StyleRoles.Caption
                    : paragraph.ClassList.Contains("manual-table-caption")
                        ? StyleRoles.TableCaption
                        : paragraph.ClassList.Contains("manual-figure-image-paragraph")
                            && NearestAncestor(paragraph, "li") is null
                            ? StyleRoles.FigureImage
                            : paragraph.ClassList.Contains("manual-table-paragraph")
                                ? StyleRoles.Table
                                : paragraph.ClassList.Contains("manual-admonition-paragraph")
                                    ? StyleRoles.Admonition
                                    : paragraph.ClassList.Contains("manual-body-ordered-item-paragraph")
                                        ? StyleRoles.OrderedList
                                        : paragraph.ClassList.Contains("manual-body-unordered-item-paragraph")
                                            ? StyleRoles.UnorderedList
                                            : paragraph.ClassList.Contains("manual-body-paragraph")
                                                ? StyleRoles.Body
                                                : null;
            if (role is not null)
            {
                AddRoleMarker(document, paragraph, role);
            }
            if (role == StyleRoles.Admonition)
            {
                AddRoleMarker(document, paragraph, ResolveAdmonitionVisualRole(paragraph));
            }

            // Fenced code and figure captions inside a list item keep their more
            // specific style without breaking the surrounding native numbering
            // context. Carry both roles through Word's HTML import so the list
            // finalizer can recognize the continuation.
            if ((role is StyleRoles.CodeBlock or StyleRoles.Caption or StyleRoles.TableCaption or StyleRoles.Admonition)
                && NearestAncestor(paragraph, "li") is { } item)
            {
                var list = NearestList(item);
                if (list?.LocalName == "ol")
                {
                    AddRoleMarker(document, paragraph, StyleRoles.OrderedList);
                }
                else if (list?.LocalName == "ul")
                {
                    AddRoleMarker(document, paragraph, StyleRoles.UnorderedList);
                }
            }
        }


        foreach (var caption in document.QuerySelectorAll("figcaption"))
        {
            AddRoleMarker(document, caption, StyleRoles.Caption);
            if (ResolveListRole(caption) is { } listRole)
            {
                AddRoleMarker(document, caption, listRole);
            }
        }

        foreach (var definitionItem in document.QuerySelectorAll("dt, dd"))
        {
            AddRoleMarker(document, definitionItem, StyleRoles.Body);
        }

        // Pandoc emits standalone Markdown images as <figure>. A marker inside
        // the figure travels with the image paragraph through Word HTML import.
        foreach (var figure in document.QuerySelectorAll("figure"))
        {
            if (ResolveListRole(figure) is { } listRole)
            {
                AddRoleMarker(document, figure, listRole);
            }
        }

        // Cover raw/direct <img> children that are not already represented by a
        // paragraph or figure marker.
        foreach (var image in document.QuerySelectorAll("img"))
        {
            if (NearestAncestor(image, "p") is null
                && NearestAncestor(image, "figure") is null
                && ResolveListRole(image) is { } listRole)
            {
                var marker = document.CreateElement("span");
                marker.ClassList.Add("md2word-role-marker");
                marker.TextContent = $"__MD2WORD_ROLE_{Guid.NewGuid():N}_{listRole}__";
                image.Before(marker);
            }
        }

        // Pandoc tight lists contain direct text in <li>; loose lists are marked on their child paragraphs.
        foreach (var item in document.QuerySelectorAll("li").Where(item => !item.Children.Any(child => child.LocalName == "p")))
        {
            var role = item.ClassList.Contains("manual-body-ordered-item")
                ? StyleRoles.OrderedList
                : item.ClassList.Contains("manual-body-unordered-item")
                    ? StyleRoles.UnorderedList
                    : null;
            if (role is not null)
            {
                AddRoleMarker(document, item, role);
            }
        }
    }

    private static string ResolveAdmonitionVisualRole(IElement paragraph)
    {
        var admonition = NearestAncestorWithClass(paragraph, "admonition");
        if (admonition?.ClassList.Contains("admonition-danger") == true)
        {
            return AdmonitionVisualDangerRole;
        }
        if (admonition?.ClassList.Contains("admonition-warning") == true)
        {
            return AdmonitionVisualWarningRole;
        }
        if (admonition?.ClassList.Contains("admonition-caution") == true)
        {
            return AdmonitionVisualCautionRole;
        }
        if (admonition?.ClassList.Contains("admonition-note") == true)
        {
            return AdmonitionVisualNoteRole;
        }
        return AdmonitionVisualGenericRole;
    }

    private static void AddRoleMarker(IDocument document, IElement element, string role)
    {
        var marker = document.CreateElement("span");
        marker.ClassList.Add("md2word-role-marker");
        marker.TextContent = $"__MD2WORD_ROLE_{Guid.NewGuid():N}_{role}__";
        element.Prepend(marker);
    }

    private static string? ResolveListRole(IElement element)
    {
        var item = NearestAncestor(element, "li");
        var list = item is null ? null : NearestList(item);
        return list?.LocalName == "ol"
            ? StyleRoles.OrderedList
            : list?.LocalName == "ul"
                ? StyleRoles.UnorderedList
                : null;
    }

    private static IElement? NearestList(IElement element)
    {
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (parent.LocalName is "ol" or "ul")
            {
                return parent;
            }
        }
        return null;
    }

    private static IElement? NearestAncestor(IElement element, string localName)
    {
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (parent.LocalName == localName)
            {
                return parent;
            }
        }
        return null;
    }

    private static IElement? NearestAncestorWithClass(IElement element, string className)
    {
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (parent.ClassList.Contains(className))
            {
                return parent;
            }
        }
        return null;
    }

    private static void NormalizeInternalAnchorBookmarks(IDocument document)
    {
        var internalLinks = document.QuerySelectorAll("a[href]")
            .Select(link => (Link: link, Href: link.GetAttribute("href")))
            .Where(candidate => candidate.Href is { Length: > 1 } href && href.StartsWith('#'))
            .ToArray();
        if (internalLinks.Length == 0)
        {
            return;
        }

        var referencedIds = internalLinks
            .Select(candidate => candidate.Href![1..])
            .ToHashSet(StringComparer.Ordinal);
        var usedBookmarkNames = document.QuerySelectorAll("[name]")
            .Select(element => element.GetAttribute("name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var nextBookmarkNumber = 1;

        foreach (var target in document.QuerySelectorAll("[id]"))
        {
            var id = target.Id;
            if (string.IsNullOrEmpty(id)
                || !referencedIds.Contains(id)
                || replacements.ContainsKey(id))
            {
                continue;
            }

            string bookmarkName;
            do
            {
                bookmarkName = $"{InternalBookmarkPrefix}{nextBookmarkNumber++:D6}";
            }
            while (!usedBookmarkNames.Add(bookmarkName));

            var bookmark = document.CreateElement("a");
            bookmark.SetAttribute("name", bookmarkName);
            target.Before(bookmark);
            replacements.Add(id, bookmarkName);
        }

        foreach (var (link, href) in internalLinks)
        {
            if (replacements.TryGetValue(href![1..], out var bookmarkName))
            {
                link.SetAttribute("href", $"#{bookmarkName}");
                link.SetAttribute(InternalLinkMarkerAttribute, "true");
            }
        }
    }

    internal static int CountGeneratedInternalLinks(string html) => Regex.Matches(
        html,
        $@"\b{Regex.Escape(InternalLinkMarkerAttribute)}=""true""",
        RegexOptions.CultureInvariant).Count;

    private static void RewriteLocalReferences(IDocument document, string sourceDirectory)
    {
        foreach (var (selector, attribute, mayLoadAutomatically) in new[]
        {
            ("img[src]", "src", true),
            ("source[src]", "src", true),
            ("video[src]", "src", true),
            ("audio[src]", "src", true),
            ("video[poster]", "poster", true),
            ("a[href]", "href", false),
        })
        {
            foreach (var element in document.QuerySelectorAll(selector))
            {
                var value = element.GetAttribute(attribute);
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith('#'))
                {
                    continue;
                }
                if (value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith("\\\\", StringComparison.Ordinal))
                {
                    throw RemoteResourceBlocked();
                }
                if (Path.IsPathRooted(value))
                {
                    element.SetAttribute(attribute, new Uri(Path.GetFullPath(value)).AbsoluteUri);
                    continue;
                }
                if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
                {
                    if (absoluteUri.IsFile && !absoluteUri.IsUnc)
                    {
                        element.SetAttribute(attribute, new Uri(Path.GetFullPath(absoluteUri.LocalPath)).AbsoluteUri);
                        continue;
                    }
                    if (!mayLoadAutomatically && absoluteUri.Scheme is "http" or "https" or "mailto")
                    {
                        continue;
                    }
                    if (mayLoadAutomatically
                        && element.LocalName == "img"
                        && Regex.IsMatch(value, @"^data:image/(?:png|jpeg|gif);base64,", RegexOptions.IgnoreCase))
                    {
                        continue;
                    }
                    throw RemoteResourceBlocked();
                }
                var fragmentIndex = value.IndexOf('#');
                var pathPart = fragmentIndex >= 0 ? value[..fragmentIndex] : value;
                var fragment = fragmentIndex >= 0 ? value[fragmentIndex..] : string.Empty;
                if (pathPart.Length == 0)
                {
                    continue;
                }
                var absolutePath = Path.GetFullPath(Path.Combine(sourceDirectory, Uri.UnescapeDataString(pathPart.Replace('/', Path.DirectorySeparatorChar))));
                element.SetAttribute(attribute, new Uri(absolutePath).AbsoluteUri + fragment);
            }
        }
    }

    private static void RejectUnsafeActiveContent(IDocument document, string css)
    {
        if (document.QuerySelector("script, iframe, object, embed, link[rel~='stylesheet']") is not null)
        {
            throw new WorkerCommandException(
                "UNSAFE_HTML_BLOCKED",
                "Active or externally loaded HTML content is disabled.",
                3,
                "word-import");
        }
        var urlBearingAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src", "href", "xlink:href", "poster", "background", "lowsrc", "dynsrc", "srcset",
            "data", "action", "formaction", "cite", "longdesc", "profile",
        };
        foreach (var element in document.All)
        {
            foreach (var attribute in element.Attributes)
            {
                if (!urlBearingAttributes.Contains(attribute.Name)
                    || string.IsNullOrWhiteSpace(attribute.Value)
                    || attribute.Value.StartsWith('#')
                    || IsHandledReference(element.LocalName, attribute.Name))
                {
                    continue;
                }
                throw new WorkerCommandException(
                    "UNSAFE_HTML_BLOCKED",
                    "An unsupported URL-bearing HTML attribute was blocked before Word import.",
                    3,
                    "word-import");
            }
        }
        RejectUnsafeCssResources(css);
        foreach (var styleElement in document.QuerySelectorAll("style"))
        {
            RejectUnsafeCssResources(styleElement.TextContent);
        }
        foreach (var styledElement in document.QuerySelectorAll("[style]"))
        {
            RejectUnsafeCssResources(styledElement.GetAttribute("style") ?? string.Empty);
        }
    }

    private static bool IsHandledReference(string elementName, string attributeName) =>
        (elementName == "a" && attributeName.Equals("href", StringComparison.OrdinalIgnoreCase))
        || (elementName == "img" && attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
        || (elementName == "source" && attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
        || (elementName == "video" && (attributeName.Equals("src", StringComparison.OrdinalIgnoreCase)
            || attributeName.Equals("poster", StringComparison.OrdinalIgnoreCase)))
        || (elementName == "audio" && attributeName.Equals("src", StringComparison.OrdinalIgnoreCase));

    private static void RejectUnsafeCssResources(string css)
    {
        var cssWithoutComments = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        if (Regex.IsMatch(cssWithoutComments, @"@\s*import\b|\\[0-9a-f]{1,6}|(?:https?|ftp|file)\s*:|(?:^|[('""\s])//|\\\\", RegexOptions.IgnoreCase))
        {
            throw RemoteResourceBlocked();
        }
        foreach (Match match in Regex.Matches(cssWithoutComments, @"url\s*\(\s*(?<value>[^)]*)\)", RegexOptions.IgnoreCase))
        {
            var value = match.Groups["value"].Value.Trim().Trim('\'', '"');
            if (!Regex.IsMatch(value, @"^data:image/(?:png|jpeg|gif);base64,", RegexOptions.IgnoreCase))
            {
                throw RemoteResourceBlocked();
            }
        }
    }

    private static WorkerCommandException RemoteResourceBlocked() => new(
        "REMOTE_RESOURCE_BLOCKED",
        "Automatic remote or network resource loading is disabled.",
        3,
        "word-import");
}
