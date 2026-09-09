using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Infrastructure;

internal static class AtomicFile
{
    public static void ReplaceFrom(string sourcePath, string destinationPath, string jobId, CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new WorkerCommandException("OUTPUT_PATH_INVALID", "The output path has no parent directory.", 2, "preparing");
        Directory.CreateDirectory(destinationDirectory);

        var safeJobId = string.Concat(jobId.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
        var stagingPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationPath)}.{safeJobId}.tmp.docx");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(sourcePath, stagingPath, overwrite: true);
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(destinationPath))
            {
                File.Replace(stagingPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(stagingPath, destinationPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WorkerCommandException("OUTPUT_WRITE_FAILED", "The verified DOCX could not be moved to the selected output path.", 4, "cleanup", retryable: true, inner: exception);
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Cleanup failures are diagnostic-only and must not mask the primary result.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failures are diagnostic-only and must not mask the primary result.
        }
    }
}
