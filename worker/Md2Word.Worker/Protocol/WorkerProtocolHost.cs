using System.Text.Json;
using Md2Word.Worker.Infrastructure;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Protocol;

internal static class WorkerProtocolHost
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(TextReader input, TextWriter output, TextWriter diagnostics)
    {
        var writer = new ProtocolWriter(output);
        string requestId = "unknown";
        try
        {
            var line = await input.ReadLineAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new WorkerCommandException("REQUEST_MISSING", "Worker stdin must begin with one JSON request frame.", 2);
            }

            using var document = ParseJson(line);
            var root = document.RootElement;
            requestId = ReadRequiredString(root, "requestId");
            var protocolVersion = ReadRequiredString(root, "protocolVersion");
            if (!string.Equals(protocolVersion, ProtocolConstants.Version, StringComparison.Ordinal))
            {
                throw new WorkerCommandException("PROTOCOL_VERSION_UNSUPPORTED", "protocolVersion must be 1.0.", 2);
            }
            var command = ReadRequiredString(root, "command");

            return command switch
            {
                "diagnose" => RunDiagnose(requestId, writer),
                "describe-capabilities" => RunDescribeCapabilities(requestId, writer),
                "validate-template" => RunValidateTemplate(root, requestId, writer),
                "convert" => await RunConvertAsync(root, requestId, input, writer, diagnostics).ConfigureAwait(false),
                "cancel" => throw new WorkerCommandException("CANCEL_WITHOUT_CONVERSION", "cancel is only valid while a convert command is running.", 2),
                _ => throw new WorkerCommandException("COMMAND_UNKNOWN", "The Worker command is not supported.", 2),
            };
        }
        catch (WorkerCommandException exception)
        {
            await diagnostics.WriteLineAsync($"{exception.Code}: {exception.Message}").ConfigureAwait(false);
            writer.Error(requestId, new WorkerError(exception.Code, exception.Message, exception.Stage, exception.Retryable));
            return exception.ExitCode;
        }
        catch (Exception exception)
        {
            await diagnostics.WriteLineAsync($"WORKER_INTERNAL_ERROR: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            writer.Error(requestId, new WorkerError("WORKER_INTERNAL_ERROR", "The Worker encountered an internal error."));
            return 10;
        }
    }

    private static int RunDiagnose(string requestId, ProtocolWriter writer)
    {
        writer.Result(requestId, new DiagnosticsService().Diagnose());
        return 0;
    }

    private static int RunDescribeCapabilities(string requestId, ProtocolWriter writer)
    {
        writer.Result(requestId, new CapabilityCatalogService().Describe());
        return 0;
    }

    private static int RunValidateTemplate(JsonElement root, string requestId, ProtocolWriter writer)
    {
        var docxPath = ReadRequiredString(root, "docxPath");
        var cssPath = ReadRequiredString(root, "cssPath");
        var outcome = new TemplateValidationService().Validate(docxPath, cssPath);
        writer.Result(requestId, outcome.Report);
        return 0;
    }

    private static async Task<int> RunConvertAsync(
        JsonElement root,
        string requestId,
        TextReader input,
        ProtocolWriter writer,
        TextWriter diagnostics)
    {
        if (!root.TryGetProperty("request", out var requestElement))
        {
            throw new WorkerCommandException("REQUEST_INVALID", "convert requires a request object.", 2);
        }
        ConversionRequest request;
        try
        {
            request = requestElement.Deserialize<ConversionRequest>(SerializerOptions)
                ?? throw new JsonException("Conversion request was null.");
        }
        catch (JsonException exception)
        {
            throw new WorkerCommandException("REQUEST_INVALID", "The conversion request does not match protocol 1.0.", 2, inner: exception);
        }

        using var conversionCancellation = new CancellationTokenSource();
        using var controlReaderCancellation = new CancellationTokenSource();
        // Console.In is a synchronized TextReader on Windows. Its token-aware
        // ReadLineAsync can block synchronously before returning a Task, so starting
        // the control loop inline would prevent the conversion from beginning until
        // stdin was closed or a cancel frame arrived. Keep the blocking read on a
        // background thread while the STA conversion proceeds.
        var controlTask = Task.Run(() => ReadControlFramesAsync(
            input,
            requestId,
            request.JobId,
            writer,
            diagnostics,
            conversionCancellation,
            controlReaderCancellation.Token));

        writer.Event(requestId, Event(request.JobId, "started", "preparing", "Conversion started."));
        try
        {
            var result = await StaThreadRunner.RunAsync(() =>
                new ConversionService().Convert(
                    request,
                    (stage, message) => writer.Event(requestId, Event(request.JobId, "stage", stage, message)),
                    conversionCancellation.Token)).ConfigureAwait(false);
            writer.Event(requestId, Event(request.JobId, "completed", "cleanup", "Conversion completed.", result));
            writer.Result(requestId, result);
            return 0;
        }
        catch (OperationCanceledException)
        {
            var result = new ConversionResult(request.JobId, "canceled", null, null, 0, []);
            writer.Event(requestId, Event(request.JobId, "canceled", "cleanup", "Conversion was canceled after cleanup.", result));
            writer.Result(requestId, result);
            return 5;
        }
        catch (WorkerCommandException exception)
        {
            var error = new WorkerError(exception.Code, exception.Message, exception.Stage, exception.Retryable);
            writer.Event(requestId, new ConversionEvent(
                request.JobId,
                DateTimeOffset.UtcNow.ToString("O"),
                "failed",
                exception.Stage,
                "error",
                null,
                exception.Message,
                null,
                error));
            await diagnostics.WriteLineAsync($"{exception.Code}: {exception.Message}").ConfigureAwait(false);
            writer.Error(requestId, error);
            return exception.ExitCode;
        }
        catch (Exception exception)
        {
            await diagnostics.WriteLineAsync($"WORKER_INTERNAL_ERROR: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            var error = new WorkerError("WORKER_INTERNAL_ERROR", "The Worker encountered an internal conversion error.", "cleanup");
            writer.Event(requestId, new ConversionEvent(
                request.JobId,
                DateTimeOffset.UtcNow.ToString("O"),
                "failed",
                "cleanup",
                "error",
                null,
                error.Message,
                null,
                error));
            writer.Error(requestId, error);
            return 10;
        }
        finally
        {
            controlReaderCancellation.Cancel();
            _ = controlTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static async Task ReadControlFramesAsync(
        TextReader input,
        string requestId,
        string jobId,
        ProtocolWriter writer,
        TextWriter diagnostics,
        CancellationTokenSource conversionCancellation,
        CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (line is null)
            {
                return;
            }
            try
            {
                using var document = ParseJson(line);
                var root = document.RootElement;
                if (ReadRequiredString(root, "protocolVersion") != ProtocolConstants.Version
                    || ReadRequiredString(root, "requestId") != requestId
                    || ReadRequiredString(root, "command") != "cancel"
                    || ReadRequiredString(root, "jobId") != jobId)
                {
                    await diagnostics.WriteLineAsync("CONTROL_FRAME_IGNORED: cancel frame did not match the active conversion.").ConfigureAwait(false);
                    continue;
                }
                if (!conversionCancellation.IsCancellationRequested)
                {
                    writer.Event(requestId, Event(jobId, "canceling", "cleanup", "Cancellation requested; Word and temporary files will be cleaned up."));
                    conversionCancellation.Cancel();
                }
            }
            catch (Exception exception) when (exception is JsonException or WorkerCommandException)
            {
                await diagnostics.WriteLineAsync("CONTROL_FRAME_IGNORED: invalid JSON control frame.").ConfigureAwait(false);
            }
        }
    }

    private static ConversionEvent Event(string jobId, string kind, string? stage, string message, ConversionResult? result = null) =>
        new(jobId, DateTimeOffset.UtcNow.ToString("O"), kind, stage, "info", null, message, result);

    private static JsonDocument ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json.TrimStart('\uFEFF'));
        }
        catch (JsonException exception)
        {
            throw new WorkerCommandException("INVALID_JSON", "Worker stdin contained invalid JSON.", 2, inner: exception);
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new WorkerCommandException("REQUEST_INVALID", $"Required protocol field is missing: {propertyName}.", 2);
        }
        return property.GetString()!;
    }
}
