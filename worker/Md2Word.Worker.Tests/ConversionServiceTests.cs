using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class ConversionServiceTests
{
    [Fact]
    public void CreatesTheTemporaryWorkDirectoryBesideTheRequestedOutput()
    {
        var managedRoot = Path.Combine(
            AppContext.BaseDirectory,
            "managed-job-root-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(managedRoot);
        string? observedWorkDirectory = null;

        try
        {
            var sourcePath = Path.Combine(managedRoot, "synthetic.md");
            File.WriteAllText(sourcePath, "Synthetic content");
            var request = new ConversionRequest(
                "location-test",
                "synthetic-template",
                sourcePath,
                Path.Combine(managedRoot, "result.docx"),
                new TemplateSnapshot(
                    Path.Combine(managedRoot, "not-used.docx"),
                    Path.Combine(managedRoot, "not-used.css"),
                    "not-used"),
                new ToolPaths("not-used-pandoc.exe"),
                new ConversionOptions(3, "off", "png"));

            Assert.Throws<WorkDirectoryObservedException>(() =>
                new ConversionService().Convert(
                    request,
                    (stage, _) =>
                    {
                        if (!string.Equals(stage, "preparing", StringComparison.Ordinal)) return;
                        observedWorkDirectory = Directory.EnumerateDirectories(
                            managedRoot,
                            ".md2word-work-*").Single();
                        throw new WorkDirectoryObservedException();
                    },
                    CancellationToken.None));

            Assert.NotNull(observedWorkDirectory);
            Assert.Equal(
                Path.GetFullPath(managedRoot),
                Path.GetFullPath(Path.GetDirectoryName(observedWorkDirectory!)!));
            Assert.False(
                Path.GetFullPath(observedWorkDirectory!).StartsWith(
                    Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "The conversion work directory must not be created under the system temporary directory.");
            Assert.False(
                Directory.Exists(observedWorkDirectory),
                "ConversionService should remove its temporary work directory when conversion exits early.");
        }
        finally
        {
            if (Directory.Exists(managedRoot)) Directory.Delete(managedRoot, recursive: true);
        }
    }

    [Fact]
    public void AllowsHtmlThatOnlyUsesResolvedHeadingLevels()
    {
        var resolved = new Dictionary<string, ResolvedStyle>(StringComparer.Ordinal)
        {
            [StyleRoles.Heading1] = new("Heading1", "heading 1"),
            [StyleRoles.Heading4] = new("Heading4", "heading 4"),
        };

        ConversionService.EnsureUsedHeadingStylesResolved(
            "<main><h1>Top</h1><h4 class=\"detail\">Detail</h4></main>",
            resolved);
    }

    [Fact]
    public void RejectsHtmlThatUsesAnUnavailableHeadingLevel()
    {
        var resolved = new Dictionary<string, ResolvedStyle>(StringComparer.Ordinal)
        {
            [StyleRoles.Heading1] = new("Heading1", "heading 1"),
        };

        var exception = Assert.Throws<WorkerCommandException>(() =>
            ConversionService.EnsureUsedHeadingStylesResolved(
                "<main><H6 id=\"unsupported\">Unsupported</H6></main>",
                resolved));

        Assert.Equal("WORD_NATIVE_STYLE_FALLBACK_MISSING", exception.Code);
        Assert.Equal("pandoc", exception.Stage);
        Assert.Contains("uses h6", exception.Message, StringComparison.Ordinal);
    }

    private sealed class WorkDirectoryObservedException : Exception;
}
