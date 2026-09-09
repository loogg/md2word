using System.Text.Json.Serialization;

namespace Md2Word.Worker.Protocol;

internal static class ProtocolConstants
{
    public const string Version = "1.0";
}

public sealed record WorkerError(string Code, string Message, string? Stage = null, bool Retryable = false);

public sealed record ConversionEvent(
    string JobId,
    string Timestamp,
    string Kind,
    string? Stage = null,
    string? Level = null,
    int? Progress = null,
    string? Message = null,
    ConversionResult? Result = null,
    WorkerError? Error = null);

public sealed record ConversionResult(
    string JobId,
    string Status,
    string? OutputPath,
    string? DiagnosticPath,
    long DurationMs,
    IReadOnlyList<string> Warnings);

public sealed record TemplateSnapshot(string DocxPath, string CssPath, string ValidationFingerprint);
public sealed record ToolPaths(string PandocPath, string? NpxPath = null, string? MermaidBrowserPath = null);
public sealed record ConversionOptions(int TocDepth, string MermaidMode, string MermaidFormat);
public sealed record ConversionRequest(
    string JobId,
    string TemplateId,
    string SourcePath,
    string OutputPath,
    TemplateSnapshot TemplateSnapshot,
    ToolPaths Tools,
    ConversionOptions Options);

public sealed record EnvironmentItem(
    string Id,
    string Name,
    string Status,
    bool Required,
    string Version,
    string Detail);

public sealed record EnvironmentStatus(string CheckedAt, string Overall, IReadOnlyList<EnvironmentItem> Items);

public sealed record ValidationIssue(
    string Code,
    string Severity,
    string Target,
    string Message,
    string? CapabilityId = null);

public sealed record TemplateCapabilities(
    bool BodyRange,
    bool CoverTitle,
    bool CoverSubtitle,
    IReadOnlyList<string> VersionTables,
    bool CodeBlockStyle);

public sealed record StyleMappingReport(
    string Role,
    int? HeadingLevel,
    string CssSelector,
    string RequestedStyleName,
    string? ResolvedStyleId,
    string? ResolvedStyleName,
    string Status);

public sealed record TemplateValidationReport(
    string Status,
    string CheckedAt,
    string ContentFingerprint,
    string Summary,
    IReadOnlyList<ValidationIssue> Issues,
    TemplateCapabilities Capabilities,
    IReadOnlyList<StyleMappingReport> StyleMappings);

internal sealed class WorkerCommandException : Exception
{
    public WorkerCommandException(string code, string message, int exitCode, string? stage = null, bool retryable = false, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        ExitCode = exitCode;
        Stage = stage;
        Retryable = retryable;
    }

    public string Code { get; }
    public int ExitCode { get; }
    public string? Stage { get; }
    public bool Retryable { get; }
}
