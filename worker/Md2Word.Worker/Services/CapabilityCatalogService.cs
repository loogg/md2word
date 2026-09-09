using System.Text.Json;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Services;

internal sealed class CapabilityCatalogService
{
    private static readonly HashSet<string> SupportedStatuses =
        new(["supported", "conditional", "limited", "unsupported"], StringComparer.Ordinal);
    private static readonly HashSet<string> SupportedStyleRoles =
        new([
            "body",
            "ordered-list",
            "unordered-list",
            "heading",
            "caption",
            "table-caption",
            "code-block",
            "inline-code",
            "table",
            "admonition",
            "figure-image",
        ], StringComparer.Ordinal);

    public JsonElement Describe()
    {
        var path = ConversionResourceLocator.Get("capabilities.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Validate(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (WorkerCommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw InvalidManifest(exception);
        }
    }

    private static void Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || ReadString(root, "schemaVersion") != "1.1"
            || string.IsNullOrWhiteSpace(ReadString(root, "productVersion"))
            || ReadString(root, "protocolVersion") != ProtocolConstants.Version
            || ReadString(root, "locale") != "zh-CN"
            || string.IsNullOrWhiteSpace(ReadString(root, "title"))
            || string.IsNullOrWhiteSpace(ReadString(root, "summary")))
        {
            throw InvalidManifest();
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var categories = ReadArray(root, "categories");
        if (categories.GetArrayLength() == 0)
        {
            throw InvalidManifest();
        }
        foreach (var category in categories.EnumerateArray())
        {
            RequireObject(category, "id", "title", "description");
            var items = ReadArray(category, "items");
            if (items.GetArrayLength() == 0)
            {
                throw InvalidManifest();
            }
            foreach (var item in items.EnumerateArray())
            {
                ValidateFeature(item, identifiers, requireSyntax: true);
            }
        }

        var frontMatter = ReadArray(root, "frontMatter");
        if (frontMatter.GetArrayLength() == 0)
        {
            throw InvalidManifest();
        }
        foreach (var metadata in frontMatter.EnumerateArray())
        {
            RequireObject(metadata, "id", "key", "type", "defaultValue", "status", "description", "example");
            RegisterId(metadata, identifiers);
            ValidateStatus(metadata);
            ReadStringArray(metadata, "allowedValues");
            ReadStringArray(metadata, "requires");
        }

        var templateContract = ReadObject(root, "templateContract");
        ValidateBookmarkArray(templateContract, "requiredBookmarks");
        ValidateBookmarkArray(templateContract, "optionalBookmarks");
        var cssRoles = ReadArray(templateContract, "cssRoles");
        if (cssRoles.GetArrayLength() == 0)
        {
            throw InvalidManifest();
        }
        foreach (var role in cssRoles.EnumerateArray())
        {
            RequireObject(role, "role", "label", "fallback");
            if (!SupportedStyleRoles.Contains(ReadString(role, "role")))
            {
                throw InvalidManifest();
            }
            ReadStringArray(role, "selectors");
        }
        ReadStringArray(templateContract, "validationNotes");

        var limitations = ReadArray(root, "limitations");
        if (limitations.GetArrayLength() == 0)
        {
            throw InvalidManifest();
        }
        foreach (var limitation in limitations.EnumerateArray())
        {
            ValidateFeature(limitation, identifiers, requireSyntax: false);
            var status = ReadString(limitation, "status");
            if (status is not ("limited" or "unsupported"))
            {
                throw InvalidManifest();
            }
        }

        var tooling = ReadObject(root, "tooling");
        RequireObject(tooling, "platform");
        ReadStringArray(tooling, "required");
        ReadStringArray(tooling, "optional");
        var pinned = ReadObject(tooling, "pinned");
        if (pinned.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String))
        {
            throw InvalidManifest();
        }
        ReadStringArray(root, "implemented");
        ReadStringArray(root, "pendingLegacyParity");
    }

    private static void ValidateFeature(JsonElement feature, HashSet<string> identifiers, bool requireSyntax)
    {
        RequireObject(feature, "id", "title", "status", "summary");
        RegisterId(feature, identifiers);
        ValidateStatus(feature);
        ReadStringArray(feature, "details");
        if (requireSyntax)
        {
            ReadStringArray(feature, "syntax");
            ReadStringArray(feature, "relatedMetadata");
        }
    }

    private static void ValidateBookmarkArray(JsonElement contract, string propertyName)
    {
        var bookmarks = ReadArray(contract, propertyName);
        if (propertyName == "requiredBookmarks" && bookmarks.GetArrayLength() == 0)
        {
            throw InvalidManifest();
        }
        foreach (var bookmark in bookmarks.EnumerateArray())
        {
            RequireObject(bookmark, "name", "description");
        }
    }

    private static void ValidateStatus(JsonElement element)
    {
        if (!SupportedStatuses.Contains(ReadString(element, "status")))
        {
            throw InvalidManifest();
        }
    }

    private static void RegisterId(JsonElement element, HashSet<string> identifiers)
    {
        if (!identifiers.Add(ReadString(element, "id")))
        {
            throw InvalidManifest();
        }
    }

    private static void RequireObject(JsonElement element, params string[] stringProperties)
    {
        if (element.ValueKind != JsonValueKind.Object
            || stringProperties.Any(property => string.IsNullOrWhiteSpace(ReadString(element, property))))
        {
            throw InvalidManifest();
        }
    }

    private static JsonElement ReadObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            throw InvalidManifest();
        }
        return property;
    }

    private static JsonElement ReadArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest();
        }
        return property;
    }

    private static JsonElement ReadStringArray(JsonElement element, string propertyName)
    {
        var property = ReadArray(element, propertyName);
        if (property.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            throw InvalidManifest();
        }
        return property;
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static WorkerCommandException InvalidManifest(Exception? inner = null) => new(
        "CAPABILITY_MANIFEST_INVALID",
        "The bundled capability manifest is missing or invalid.",
        3,
        "preparing",
        inner: inner);
}
