using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class ConversionResourceLocatorTests
{
    [Fact]
    public void ResolvesAFileInsideTheControlledResourceRoot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var filters = Directory.CreateDirectory(Path.Combine(root, "filters")).FullName;
            var expected = Path.Combine(filters, "filter.lua");
            File.WriteAllText(expected, "return {}", new System.Text.UTF8Encoding(false));

            var actual = ConversionResourceLocator.GetFromRoot(root, Path.Combine("filters", "filter.lua"));

            Assert.Equal(Path.GetFullPath(expected), actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsTraversalOutsideTheControlledResourceRoot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var exception = Assert.Throws<WorkerCommandException>(() =>
                ConversionResourceLocator.GetFromRoot(root, Path.Combine("..", "outside.lua")));

            Assert.Equal("CONVERSION_RESOURCE_PATH_INVALID", exception.Code);
            Assert.Equal("preparing", exception.Stage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReportsAMissingBundledResourceWithAStableError()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var exception = Assert.Throws<WorkerCommandException>(() =>
                ConversionResourceLocator.GetFromRoot(root, "missing.lua"));

            Assert.Equal("CONVERSION_RESOURCE_MISSING", exception.Code);
            Assert.Equal("preparing", exception.Stage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"md2word-resource-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
