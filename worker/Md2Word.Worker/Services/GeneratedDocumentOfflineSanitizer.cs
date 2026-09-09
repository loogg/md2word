using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Md2Word.Worker.Protocol;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DrawingBlip = DocumentFormat.OpenXml.Drawing.Blip;
using DrawingWordprocessing = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using SvgBlip = DocumentFormat.OpenXml.Office2019.Drawing.SVG.SVGBlip;
using VmlImageData = DocumentFormat.OpenXml.Vml.ImageData;
using WordBookmarkEnd = DocumentFormat.OpenXml.Wordprocessing.BookmarkEnd;
using WordBookmarkStart = DocumentFormat.OpenXml.Wordprocessing.BookmarkStart;
using WordRun = DocumentFormat.OpenXml.Wordprocessing.Run;

namespace Md2Word.Worker.Services;

internal sealed record EmbeddedLocalImageRecovery(
    string Marker,
    string LocalPath,
    string OriginalAlternativeText,
    string OriginalTitle);

internal sealed record GeneratedDocumentOfflineSanitizationResult(
    IReadOnlyList<EmbeddedLocalImageRecovery> BodyImageRecoveries);

/// <summary>
/// Converts the local linked images that Word creates while importing HTML into
/// package-owned image parts. This is intentionally narrower than template
/// validation: only recognized DrawingML/VML references to readable, local
/// file: image targets are rewritten. Everything else remains external so the
/// generated-document security check can reject it.
/// </summary>
internal sealed class GeneratedDocumentOfflineSanitizer
{
    private const string ImageRelationshipSuffix = "/image";
    private const string ImageRecoveryMarkerPrefix = "MD2WIMG_";
    private const string OfficeRelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const long MaximumImageBytes = 100L * 1024 * 1024;

    public GeneratedDocumentOfflineSanitizationResult EmbedLinkedLocalImages(string docxPath)
    {
        using var document = WordprocessingDocument.Open(docxPath, true);
        var mainPart = document.MainDocumentPart
            ?? throw new WorkerCommandException(
                "OUTPUT_DOCX_INVALID",
                "The generated DOCX has no main document part.",
                4,
                "openxml-finalize");
        var bodyImageRecoveries = new List<EmbeddedLocalImageRecovery>();

        foreach (var part in new[] { (OpenXmlPart)mainPart }.Concat(EnumerateParts(mainPart)))
        {
            EmbedLinkedLocalImages(
                part,
                ReferenceEquals(part, mainPart),
                bodyImageRecoveries);
        }
        return new GeneratedDocumentOfflineSanitizationResult(bodyImageRecoveries);
    }

    public void RestoreRecoveredImageMetadata(
        string docxPath,
        IReadOnlyList<EmbeddedLocalImageRecovery> recoveries)
    {
        if (recoveries.Count == 0)
        {
            return;
        }

        using var document = WordprocessingDocument.Open(docxPath, true);
        var root = document.MainDocumentPart?.Document
            ?? throw ImageRecoveryFailed();
        foreach (var recovery in recoveries)
        {
            var elements = root.Descendants().ToArray();
            var starts = elements.OfType<WordBookmarkStart>()
                .Where(bookmark => string.Equals(
                    bookmark.Name?.Value,
                    recovery.Marker,
                    StringComparison.Ordinal))
                .ToArray();
            if (starts.Length != 1)
            {
                throw ImageRecoveryFailed();
            }
            var start = starts[0];
            var ends = elements.OfType<WordBookmarkEnd>()
                .Where(bookmark => string.Equals(
                    bookmark.Id?.Value,
                    start.Id?.Value,
                    StringComparison.Ordinal))
                .ToArray();
            if (ends.Length != 1)
            {
                throw ImageRecoveryFailed();
            }
            var end = ends[0];
            var startIndex = Array.IndexOf(elements, start);
            var endIndex = Array.IndexOf(elements, end);
            if (startIndex < 0 || endIndex <= startIndex)
            {
                throw ImageRecoveryFailed();
            }
            var properties = elements
                .Skip(startIndex + 1)
                .Take(endIndex - startIndex - 1)
                .OfType<DrawingWordprocessing.DocProperties>()
                .ToArray();
            if (properties.Length != 1)
            {
                throw ImageRecoveryFailed();
            }

            properties[0].Description = string.IsNullOrEmpty(recovery.OriginalAlternativeText)
                ? null
                : recovery.OriginalAlternativeText;
            properties[0].Title = string.IsNullOrEmpty(recovery.OriginalTitle)
                ? null
                : recovery.OriginalTitle;
            start.Remove();
            end.Remove();
        }
        root.Save();
    }

    private static void EmbedLinkedLocalImages(
        OpenXmlPart ownerPart,
        bool collectBodyImageRecoveries,
        List<EmbeddedLocalImageRecovery> bodyImageRecoveries)
    {
        var root = GetRootElement(ownerPart);
        if (root is null)
        {
            return;
        }

        var changed = false;
        var nextBookmarkId = GetNextBookmarkId(root);
        foreach (var relationship in ownerPart.ExternalRelationships
                     .Where(IsImageRelationship)
                     .ToArray())
        {
            var drawingReferences = root.Descendants<DrawingBlip>()
                .Where(blip => string.Equals(blip.Link?.Value, relationship.Id, StringComparison.Ordinal))
                .ToArray();
            var svgReferences = root.Descendants<SvgBlip>()
                .Where(blip => string.Equals(blip.Link?.Value, relationship.Id, StringComparison.Ordinal))
                .ToArray();
            var vmlReferences = root.Descendants<VmlImageData>()
                .Where(image => string.Equals(image.RelationshipId?.Value, relationship.Id, StringComparison.Ordinal))
                .ToArray();
            var recognizedReferenceCount = drawingReferences.Length + svgReferences.Length + vmlReferences.Length;
            if (recognizedReferenceCount == 0
                || HasUnknownRelationshipReference(root, relationship.Id, recognizedReferenceCount))
            {
                continue;
            }

            using var imageStream = TryOpenLocalImage(
                relationship.Uri,
                out var contentType,
                out var localPath);
            if (imageStream is null || contentType is null || localPath is null)
            {
                continue;
            }

            ImagePart? imagePart = null;
            try
            {
                imagePart = AddImagePart(ownerPart, contentType);
                if (imagePart is null)
                {
                    continue;
                }

                imagePart.FeedData(imageStream);
                var embeddedRelationshipId = ownerPart.GetIdOfPart(imagePart);
                foreach (var blip in drawingReferences)
                {
                    blip.Embed = embeddedRelationshipId;
                    blip.Link = null;
                }
                foreach (var blip in svgReferences)
                {
                    blip.Embed = embeddedRelationshipId;
                    blip.Link = null;
                }
                foreach (var image in vmlReferences)
                {
                    image.RelationshipId = embeddedRelationshipId;
                }
                if (collectBodyImageRecoveries)
                {
                    AddBodyImageRecoveries(
                        drawingReferences,
                        svgReferences,
                        localPath,
                        ref nextBookmarkId,
                        bodyImageRecoveries);
                }

                ownerPart.DeleteExternalRelationship(relationship.Id);
                changed = true;
                imagePart = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OpenXmlPackageException)
            {
                if (imagePart is not null)
                {
                    TryDeletePart(ownerPart, imagePart);
                }
                // Leave the external relationship intact. The mandatory output
                // security check will reject the document without exposing its URI.
            }
        }

        if (changed)
        {
            root.Save();
        }
    }

    private static bool IsImageRelationship(ExternalRelationship relationship) =>
        relationship.RelationshipType.EndsWith(ImageRelationshipSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool HasUnknownRelationshipReference(
        OpenXmlPartRootElement root,
        string relationshipId,
        int recognizedReferenceCount)
    {
        var allReferences = root.Descendants()
            .Prepend(root)
            .SelectMany(element => element.GetAttributes())
            .Count(attribute =>
                string.Equals(attribute.NamespaceUri, OfficeRelationshipNamespace, StringComparison.Ordinal)
                && string.Equals(attribute.Value, relationshipId, StringComparison.Ordinal));
        return allReferences != recognizedReferenceCount;
    }

    private static FileStream? TryOpenLocalImage(
        Uri target,
        out string? contentType,
        out string? localPath)
    {
        contentType = null;
        localPath = null;
        if (!target.IsAbsoluteUri || !target.IsFile || target.IsUnc)
        {
            return null;
        }

        FileStream? stream = null;
        try
        {
            var decodedPath = DecodeWordLocalImagePath(target.LocalPath);
            if (string.IsNullOrWhiteSpace(decodedPath)
                || decodedPath.StartsWith("\\\\", StringComparison.Ordinal)
                || decodedPath.StartsWith("//", StringComparison.Ordinal))
            {
                return null;
            }

            var file = new FileInfo(System.IO.Path.GetFullPath(decodedPath));
            if (!file.Exists || file.Length <= 0 || file.Length > MaximumImageBytes || IsRemoteLinkTarget(file))
            {
                return null;
            }

            stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            contentType = DetectImageContentType(stream);
            if (contentType is null)
            {
                stream.Dispose();
                return null;
            }
            stream.Position = 0;
            localPath = file.FullName;
            return stream;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or UriFormatException)
        {
            stream?.Dispose();
            localPath = null;
            return null;
        }
    }

    private static void AddBodyImageRecoveries(
        IEnumerable<DrawingBlip> drawingReferences,
        IEnumerable<SvgBlip> svgReferences,
        string localPath,
        ref int nextBookmarkId,
        List<EmbeddedLocalImageRecovery> recoveries)
    {
        var runs = new HashSet<WordRun>();
        foreach (var reference in drawingReferences.Cast<OpenXmlElement>()
                     .Concat(svgReferences.Cast<OpenXmlElement>()))
        {
            var run = reference.Ancestors<WordRun>().FirstOrDefault();
            if (run is null || !runs.Add(run))
            {
                continue;
            }
            var properties = FindDocProperties(reference);
            var marker = $"{ImageRecoveryMarkerPrefix}{Guid.NewGuid():N}";
            var originalAlternativeText = properties?.Description?.Value ?? string.Empty;
            var originalTitle = properties?.Title?.Value ?? string.Empty;
            var bookmarkId = nextBookmarkId++.ToString(System.Globalization.CultureInfo.InvariantCulture);
            run.InsertBeforeSelf(new WordBookmarkStart
            {
                Id = bookmarkId,
                Name = marker,
            });
            run.InsertAfterSelf(new WordBookmarkEnd { Id = bookmarkId });
            recoveries.Add(new EmbeddedLocalImageRecovery(
                marker,
                localPath,
                originalAlternativeText,
                originalTitle));
        }
    }

    private static DrawingWordprocessing.DocProperties? FindDocProperties(OpenXmlElement reference)
    {
        OpenXmlElement? container = reference.Ancestors<DrawingWordprocessing.Inline>().FirstOrDefault();
        container ??= reference.Ancestors<DrawingWordprocessing.Anchor>().FirstOrDefault();
        return container?.Descendants<DrawingWordprocessing.DocProperties>().FirstOrDefault();
    }

    private static int GetNextBookmarkId(OpenXmlPartRootElement root)
    {
        var maximum = root.Descendants<WordBookmarkStart>()
            .Select(bookmark => int.TryParse(bookmark.Id?.Value, out var id) ? id : -1)
            .DefaultIfEmpty(-1)
            .Max();
        return maximum == int.MaxValue ? 0 : maximum + 1;
    }

    private static WorkerCommandException ImageRecoveryFailed() => new(
        "IMAGE_SIZE_FINALIZATION_FAILED",
        "An embedded local image could not be restored from Word's linked-image placeholder.",
        4,
        "openxml-finalize");

    private static string DecodeWordLocalImagePath(string localPath)
    {
        // Word may escape the percent signs of an already escaped file URI
        // while importing HTML. For example, a Chinese UTF-8 segment written
        // as %E9... becomes %25E9... in document.xml.rels. Uri.LocalPath
        // removes the outer layer, so decode the remaining URL-encoded path
        // once more before checking the local file.
        var decoded = Uri.UnescapeDataString(localPath);
        return string.Equals(decoded, localPath, StringComparison.Ordinal)
            ? localPath
            : decoded;
    }

    private static bool IsRemoteLinkTarget(FileInfo file)
    {
        if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        var target = file.ResolveLinkTarget(returnFinalTarget: true);
        if (target is null)
        {
            return true;
        }
        var fullName = target.FullName;
        return fullName.StartsWith("\\\\", StringComparison.Ordinal)
            || fullName.StartsWith("//", StringComparison.Ordinal);
    }

    private static string? DetectImageContentType(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[48];
        var read = stream.Read(buffer);
        ReadOnlySpan<byte> header = buffer[..read];

        if (header.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (header.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8)) return "image/gif";
        if (header.StartsWith("BM"u8)) return "image/bmp";
        if (header.StartsWith(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) || header.StartsWith(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })) return "image/tiff";
        if (header.Length >= 44 && header[40..44].SequenceEqual(new byte[] { 0x20, 0x45, 0x4D, 0x46 })) return "image/x-emf";
        if (header.StartsWith(new byte[] { 0xD7, 0xCD, 0xC6, 0x9A }) || header.StartsWith(new byte[] { 0x01, 0x00, 0x09, 0x00 })) return "image/x-wmf";
        stream.Position = 0;
        if (IsSafeSvg(stream)) return "image/svg+xml";
        return null;
    }

    private static bool IsSafeSvg(Stream stream)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumImageBytes,
                CloseInput = false,
            };
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            if (document.Root is null || !string.Equals(document.Root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var blockedElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "script", "foreignObject", "iframe", "object", "embed",
            };
            foreach (var element in document.Descendants())
            {
                if (blockedElements.Contains(element.Name.LocalName))
                {
                    return false;
                }
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    if (attribute.Name.LocalName is "href" or "src"
                        && !string.IsNullOrWhiteSpace(attribute.Value)
                        && !attribute.Value.TrimStart().StartsWith('#'))
                    {
                        return false;
                    }
                    if (attribute.Name.LocalName == "style" && ContainsUnsafeSvgCss(attribute.Value))
                    {
                        return false;
                    }
                }
                if (element.Name.LocalName == "style" && ContainsUnsafeSvgCss(element.Value))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static bool ContainsUnsafeSvgCss(string css)
    {
        if (Regex.IsMatch(css, @"@\s*import\b", RegexOptions.IgnoreCase))
        {
            return true;
        }
        foreach (Match match in Regex.Matches(css, "url\\s*\\(\\s*['\\\"]?(?<value>[^)'\\\"]+)", RegexOptions.IgnoreCase))
        {
            if (!match.Groups["value"].Value.Trim().StartsWith('#'))
            {
                return true;
            }
        }
        return false;
    }

    private static ImagePart? AddImagePart(OpenXmlPart ownerPart, string contentType) => ownerPart switch
    {
        MainDocumentPart part => part.AddImagePart(contentType),
        HeaderPart part => part.AddImagePart(contentType),
        FooterPart part => part.AddImagePart(contentType),
        FootnotesPart part => part.AddImagePart(contentType),
        EndnotesPart part => part.AddImagePart(contentType),
        WordprocessingCommentsPart part => part.AddImagePart(contentType),
        _ => null,
    };

    private static OpenXmlPartRootElement? GetRootElement(OpenXmlPart part) => part switch
    {
        MainDocumentPart mainPart => mainPart.Document,
        HeaderPart headerPart => headerPart.Header,
        FooterPart footerPart => footerPart.Footer,
        FootnotesPart footnotesPart => footnotesPart.Footnotes,
        EndnotesPart endnotesPart => endnotesPart.Endnotes,
        WordprocessingCommentsPart commentsPart => commentsPart.Comments,
        _ => null,
    };

    private static void TryDeletePart(OpenXmlPartContainer owner, OpenXmlPart part)
    {
        try
        {
            owner.DeletePart(part);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OpenXmlPackageException)
        {
            // The output remains unpublished and will fail the mandatory check.
        }
    }

    private static IEnumerable<OpenXmlPart> EnumerateParts(OpenXmlPartContainer container) =>
        EnumerateParts(container, new HashSet<Uri>());

    private static IEnumerable<OpenXmlPart> EnumerateParts(OpenXmlPartContainer container, HashSet<Uri> visited)
    {
        foreach (var pair in container.Parts)
        {
            if (!visited.Add(pair.OpenXmlPart.Uri))
            {
                continue;
            }
            yield return pair.OpenXmlPart;
            foreach (var nested in EnumerateParts(pair.OpenXmlPart, visited))
            {
                yield return nested;
            }
        }
    }
}
