using System.Diagnostics;
using DocumentFormat.OpenXml.Packaging;
using Md2Word.Worker.Infrastructure;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal sealed class ConversionService
{
    private const string BaselineCompatibilityWarning =
        "This Worker implements the audited Pandoc/Lua semantics, pinned Mermaid PNG/SVG rendering with adjacent captions, CSS-to-Word block and inline-code style mapping with post-Word style-ID rebinding, template body assembly, native Word list and heading numbering, repeat-table-header control, basic unmerged and regular rectangular merged body-table width/border/cell-spacing finalization, CSS-body-styled empty separators between adjacent tables, explicit front-matter updates, local image embedding, body inline-image width constraints, Word-safe internal-link rewriting, and fixed semantic admonition background/left-border callouts. Richer legacy admonition layouts, irregular-grid and advanced merged-table width finalization, floating/text-box/multi-column/table-cell image layout, and full WordDOM visual parity still require separate acceptance evidence.";

    private readonly TemplateValidationService validationService = new();
    private readonly PandocMetadataReader metadataReader = new();
    private readonly PandocService pandocService = new();
    private readonly MermaidRenderingService mermaidRenderingService = new();
    private readonly HtmlConversionService htmlService = new();
    private readonly WordAutomationService wordService = new();
    private readonly GeneratedDocumentOfflineSanitizer offlineSanitizer = new();
    private readonly OpenXmlFinalizationPipeline openXmlFinalizationPipeline = new();
    private readonly OpenXmlTemplateMetadataFinalizer metadataFinalizer = new();

    public ConversionResult Convert(
        ConversionRequest request,
        Action<string, string> emitStage,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        ValidateRequest(request);
        var jobRoot = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))
            ?? throw new WorkerCommandException("OUTPUT_PATH_INVALID", "The output directory is invalid.", 2, "preparing");
        Directory.CreateDirectory(jobRoot);
        var jobDirectory = Path.Combine(jobRoot, $".md2word-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobDirectory);
        var htmlPath = Path.Combine(jobDirectory, "content.html");
        var mermaidHtmlPath = Path.Combine(jobDirectory, "content.mermaid.html");
        var numberedHtmlPath = Path.Combine(jobDirectory, "content.numbered.html");
        var wordReadyHtmlPath = Path.Combine(jobDirectory, "content.worddom.html");
        var workingDocxPath = Path.Combine(jobDirectory, "result.docx");

        try
        {
            var warnings = BuildCompatibilityWarnings();
            emitStage("preparing", "Validating the immutable template and CSS snapshot.");
            cancellationToken.ThrowIfCancellationRequested();
            var validation = validationService.Validate(request.TemplateSnapshot.DocxPath, request.TemplateSnapshot.CssPath);
            if (validation.Report.Status == "invalid")
            {
                var firstError = validation.Report.Issues.FirstOrDefault(issue => issue.Severity == "error");
                throw new WorkerCommandException(
                    firstError?.Code ?? "TEMPLATE_INVALID",
                    firstError?.Message ?? "Template validation failed.",
                    4,
                    "preparing");
            }
            if (!string.Equals(validation.Report.ContentFingerprint, request.TemplateSnapshot.ValidationFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkerCommandException("VALIDATION_STALE", "The template or CSS changed after validation.", 4, "preparing", retryable: true);
            }

            emitStage("metadata", "Reading explicit cover and version-table metadata through Pandoc.");
            var metadata = metadataReader.ReadAsync(
                request.Tools.PandocPath,
                request.SourcePath,
                cancellationToken).GetAwaiter().GetResult();
            OpenXmlTemplateMetadataFinalizer.EnsureCapabilities(validation.Report.Capabilities, metadata);

            emitStage("pandoc", "Converting Markdown to standalone HTML with Pandoc.");
            pandocService.ConvertToHtmlAsync(
                request.Tools.PandocPath,
                request.SourcePath,
                htmlPath,
                request.Options.TocDepth,
                cancellationToken).GetAwaiter().GetResult();

            cancellationToken.ThrowIfCancellationRequested();
            var html = File.ReadAllText(htmlPath);
            EnsureUsedHeadingStylesResolved(html, validation.ResolvedRoleStyles);
            if (request.Options.MermaidMode != "off"
                && System.Text.RegularExpressions.Regex.IsMatch(
                    html,
                    @"<pre\b[^>]*class\s*=\s*['""][^'""]*\bmermaid\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                emitStage("mermaid", "Rendering Mermaid diagrams and binding adjacent captions.");
            }
            var mermaid = mermaidRenderingService.RenderAsync(
                html,
                Path.Combine(jobDirectory, "mermaid-assets"),
                request.Tools.NpxPath,
                request.Tools.MermaidBrowserPath,
                request.Options.MermaidMode,
                request.Options.MermaidFormat,
                cancellationToken).GetAwaiter().GetResult();
            warnings.AddRange(mermaid.Warnings);
            File.WriteAllText(mermaidHtmlPath, mermaid.Html, new System.Text.UTF8Encoding(false));

            cancellationToken.ThrowIfCancellationRequested();
            pandocService.ApplyFigureNumberingAsync(
                request.Tools.PandocPath,
                mermaidHtmlPath,
                numberedHtmlPath,
                metadata.FigureCaptions,
                cancellationToken).GetAwaiter().GetResult();

            html = File.ReadAllText(numberedHtmlPath);
            var css = File.ReadAllText(request.TemplateSnapshot.CssPath);
            var sourceDirectory = Path.GetDirectoryName(request.SourcePath) ?? Directory.GetCurrentDirectory();
            var transformedHtml = htmlService.TransformAsync(
                html,
                css,
                validation.ParsedStyleMap,
                sourceDirectory,
                cancellationToken).GetAwaiter().GetResult();
            var expectedGeneratedInternalLinks = HtmlConversionService.CountGeneratedInternalLinks(transformedHtml);
            File.WriteAllText(wordReadyHtmlPath, transformedHtml, new System.Text.UTF8Encoding(false));

            cancellationToken.ThrowIfCancellationRequested();
            emitStage("word-import", "Importing the Word-ready HTML in a dedicated Word instance.");
            wordService.BuildTemplateDocument(
                wordReadyHtmlPath,
                request.TemplateSnapshot.DocxPath,
                workingDocxPath,
                metadata,
                validation.ResolvedRoleStyles,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            emitStage("openxml-finalize", "Applying template paragraph styles to native Word lists.");
            offlineSanitizer.EmbedLinkedLocalImages(workingDocxPath);
            TemplateValidationService.EnsureGeneratedDocumentIsOfflineSafe(workingDocxPath);
            var wordSavedRoleStyles = OpenXmlResolvedStyleRebinder.Rebind(
                workingDocxPath,
                validation.ResolvedRoleStyles);
            openXmlFinalizationPipeline.Finalize(
                workingDocxPath,
                wordSavedRoleStyles,
                bodyOnly: true,
                repeatTableHeaders: metadata.WordRepeatTableHeaders);
            metadataFinalizer.Finalize(workingDocxPath, metadata);
            OpenXmlInternalLinkValidator.Validate(workingDocxPath, expectedGeneratedInternalLinks);
            EnsureReadableOutput(workingDocxPath);

            cancellationToken.ThrowIfCancellationRequested();
            emitStage("cleanup", "Moving the verified DOCX into the selected output location.");
            AtomicFile.ReplaceFrom(workingDocxPath, request.OutputPath, request.JobId, cancellationToken);
            stopwatch.Stop();
            return new ConversionResult(
                request.JobId,
                "succeeded",
                request.OutputPath,
                null,
                stopwatch.ElapsedMilliseconds,
                warnings);
        }
        finally
        {
            TryDeleteJobDirectory(jobRoot, jobDirectory);
        }
    }

    private static void ValidateRequest(ConversionRequest request)
    {
        if (request.TemplateSnapshot is null || request.Tools is null || request.Options is null
            || string.IsNullOrWhiteSpace(request.JobId)
            || string.IsNullOrWhiteSpace(request.TemplateId)
            || string.IsNullOrWhiteSpace(request.SourcePath)
            || string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new WorkerCommandException("REQUEST_INVALID", "Conversion request fields are incomplete.", 2, "preparing");
        }
        if (!File.Exists(request.SourcePath)
            || !(string.Equals(Path.GetExtension(request.SourcePath), ".md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(request.SourcePath), ".markdown", StringComparison.OrdinalIgnoreCase)))
        {
            throw new WorkerCommandException("SOURCE_FILE_UNREADABLE", "The Markdown source does not exist or has an unsupported extension.", 2, "preparing");
        }
        if (!string.Equals(Path.GetExtension(request.OutputPath), ".docx", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkerCommandException("OUTPUT_EXTENSION_INVALID", "The output file must use the .docx extension.", 2, "preparing");
        }
        if (request.Options.TocDepth is < 1 or > 6
            || request.Options.MermaidMode is not ("auto" or "off" or "required")
            || request.Options.MermaidFormat is not ("png" or "svg"))
        {
            throw new WorkerCommandException("REQUEST_INVALID", "Conversion options contain an unsupported value.", 2, "preparing");
        }

        var source = Path.GetFullPath(request.SourcePath);
        var template = Path.GetFullPath(request.TemplateSnapshot.DocxPath);
        var output = Path.GetFullPath(request.OutputPath);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase)
            || string.Equals(template, output, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, template, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkerCommandException("PATH_COLLISION", "Source, template, and output files must be different.", 2, "preparing");
        }
    }

    private static List<string> BuildCompatibilityWarnings()
    {
        return [BaselineCompatibilityWarning];
    }

    internal static void EnsureUsedHeadingStylesResolved(
        string html,
        IReadOnlyDictionary<string, ResolvedStyle> resolvedRoleStyles)
    {
        var headingRoles = new[]
        {
            StyleRoles.Heading1,
            StyleRoles.Heading2,
            StyleRoles.Heading3,
            StyleRoles.Heading4,
            StyleRoles.Heading5,
            StyleRoles.Heading6,
        };
        for (var index = 0; index < headingRoles.Length; index++)
        {
            var level = index + 1;
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    html,
                    $@"<h{level}(?:\s|>)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                || resolvedRoleStyles.ContainsKey(headingRoles[index]))
            {
                continue;
            }

            throw new WorkerCommandException(
                "WORD_NATIVE_STYLE_FALLBACK_MISSING",
                $"The converted Markdown uses h{level}, but CSS does not map it and the template has no usable Word-native paragraph style fallback.",
                4,
                "pandoc");
        }
    }

    private static void EnsureReadableOutput(string path)
    {
        try
        {
            using var document = WordprocessingDocument.Open(path, false);
            if (document.MainDocumentPart?.Document?.Body is null)
            {
                throw new InvalidDataException();
            }
            EnsureBodyBookmarkContract(document.MainDocumentPart.Document.Body);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OpenXmlPackageException)
        {
            throw new WorkerCommandException("OUTPUT_DOCX_INVALID", "The generated DOCX package failed verification.", 4, "openxml-finalize", inner: exception);
        }
    }

    private static void EnsureBodyBookmarkContract(DocumentFormat.OpenXml.Wordprocessing.Body body)
    {
        var starts = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.BookmarkStart>().ToArray();
        var bodyStartIndexes = starts
            .Select((bookmark, index) => (bookmark, index))
            .Where(item => item.bookmark.Name?.Value == TemplateValidationService.BodyStartBookmark)
            .ToArray();
        var bodyEndIndexes = starts
            .Select((bookmark, index) => (bookmark, index))
            .Where(item => item.bookmark.Name?.Value == TemplateValidationService.BodyEndBookmark)
            .ToArray();
        if (bodyStartIndexes.Length != 1
            || bodyEndIndexes.Length != 1
            || bodyStartIndexes[0].index >= bodyEndIndexes[0].index)
        {
            throw new InvalidDataException("The generated DOCX body bookmark contract is invalid.");
        }

        var bookmarkEnds = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.BookmarkEnd>()
            .Select(bookmark => bookmark.Id?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (!bookmarkEnds.Contains(bodyStartIndexes[0].bookmark.Id?.Value)
            || !bookmarkEnds.Contains(bodyEndIndexes[0].bookmark.Id?.Value))
        {
            throw new InvalidDataException("The generated DOCX body bookmarks are not paired.");
        }
    }

    private static void TryDeleteJobDirectory(string jobRoot, string jobDirectory)
    {
        try
        {
            var root = Path.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(jobDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(jobDirectory))
            {
                Directory.Delete(jobDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cleanup warning is intentionally not allowed to turn a completed conversion into failure.
        }
    }
}
