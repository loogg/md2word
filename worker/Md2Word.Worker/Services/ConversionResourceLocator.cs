using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal static class ConversionResourceLocator
{
    private const string ResourceRootEnvironmentVariable = "MD2WORD_CONVERSION_RESOURCES";

    public static string Get(string fileName)
    {
        var configuredRoot = Environment.GetEnvironmentVariable(ResourceRootEnvironmentVariable);
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppContext.BaseDirectory, "resources", "conversion")
            : configuredRoot;
        return GetFromRoot(root, fileName);
    }

    internal static string GetFromRoot(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(root)
            || !Path.IsPathFullyQualified(root)
            || string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathFullyQualified(fileName))
        {
            throw InvalidPath();
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, fileName));
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidPath();
        }
        if (!File.Exists(path))
        {
            throw new WorkerCommandException(
                "CONVERSION_RESOURCE_MISSING",
                "A required bundled conversion resource is missing.",
                3,
                "preparing");
        }
        return path;
    }

    private static WorkerCommandException InvalidPath() => new(
        "CONVERSION_RESOURCE_PATH_INVALID",
        "The bundled conversion resource path is invalid.",
        3,
        "preparing");
}
