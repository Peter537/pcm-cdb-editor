using System.ComponentModel;
using System.Diagnostics;
using PcmCdbEditor.Application;
using PcmCdbEditor.Infrastructure.Internal;

namespace PcmCdbEditor.Infrastructure.Conversion;

public sealed class CdbConverter : ICdbConverter
{
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);

    private readonly string _executablePath;

    public CdbConverter(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
    }

    public async Task<ConversionResult> ExportToSqliteAsync(
        string workingCdbPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var input = RequireInput(workingCdbPath, ".cdb", stopwatch);
        var output = Path.ChangeExtension(input, ".sqlite");
        EnsureAvailable(output, stopwatch);
        var diagnostics = await RunAsync(
                ["-a", "-export", input],
                [input, output],
                timeout,
                stopwatch,
                cancellationToken)
            .ConfigureAwait(false);
        RequireOutput(output, diagnostics, stopwatch);
        return new ConversionResult(output, diagnostics with { Duration = stopwatch.Elapsed });
    }

    public async Task<ConversionResult> ImportToCdbAsync(
        string workingSqlitePath,
        string temporaryCdbDestination,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var input = RequireInput(workingSqlitePath, ".sqlite", stopwatch);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryCdbDestination);
        var output = Path.GetFullPath(temporaryCdbDestination);
        if (!Path.GetExtension(output).Equals(".cdb", StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(output) is null)
        {
            throw Failure(ConverterFailureCategory.InvalidInput,
                "The converter destination must be a .cdb file in a directory.", stopwatch);
        }

        EnsureAvailable(output, stopwatch);
        var basePath = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output));
        var stagedSqlite = basePath + ".sqlite";
        EnsureAvailable(stagedSqlite, stopwatch);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            File.Copy(input, stagedSqlite, overwrite: false);
            var diagnostics = await RunAsync(
                    ["-a", "-import", basePath],
                    [input, output, basePath, stagedSqlite],
                    timeout,
                    stopwatch,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireOutput(output, diagnostics, stopwatch);
            return new ConversionResult(output, diagnostics with { Duration = stopwatch.Elapsed });
        }
        catch (CdbConversionException)
        {
            TryDelete(output);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(output);
            throw Failure(
                ConverterFailureCategory.FileSystem,
                "The converter staging files could not be prepared safely.",
                stopwatch,
                innerException: exception);
        }
        finally
        {
            TryDelete(stagedSqlite);
        }
    }

    private async Task<ConverterDiagnostics> RunAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<string> sensitivePaths,
        TimeSpan timeout,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw Failure(
                ConverterFailureCategory.Cancelled,
                "The SQLite exporter operation was cancelled.",
                stopwatch,
                innerException: new OperationCanceledException(cancellationToken));
        }

        if (!File.Exists(_executablePath))
        {
            throw Failure(
                ConverterFailureCategory.MissingExecutable,
                "The configured SQLite exporter executable was not found.",
                stopwatch);
        }

        var effectiveTimeout = ValidateTimeout(timeout);
        try
        {
            var result = await BoundedProcessRunner.RunAsync(
                    _executablePath,
                    arguments,
                    effectiveTimeout,
                    sensitivePaths.Append(_executablePath).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            var diagnostics = new ConverterDiagnostics(
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                stopwatch.Elapsed);
            if (result.ExitCode != 0)
            {
                throw new CdbConversionException(new ConversionFailure(
                    ConverterFailureCategory.NonZeroExit,
                    "The SQLite exporter reported a failure.",
                    diagnostics));
            }

            return diagnostics;
        }
        catch (BoundedProcessTimeoutException exception)
        {
            var diagnostics = new ConverterDiagnostics(
                null,
                exception.StandardOutput,
                exception.StandardError,
                stopwatch.Elapsed);
            throw new CdbConversionException(new ConversionFailure(
                ConverterFailureCategory.TimedOut,
                "The SQLite exporter exceeded the 10-minute operation limit.",
                diagnostics), exception);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw Failure(
                ConverterFailureCategory.Cancelled,
                "The SQLite exporter operation was cancelled.",
                stopwatch,
                innerException: exception);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw Failure(
                ConverterFailureCategory.StartFailure,
                "The SQLite exporter process could not be started.",
                stopwatch,
                innerException: exception);
        }
    }

    private static string RequireInput(string path, string extension, Stopwatch stopwatch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(ConverterFailureCategory.InvalidInput,
                $"The converter input must be a {extension} file.", stopwatch);
        }

        var information = new FileInfo(fullPath);
        if (!information.Exists || information.Length == 0)
        {
            throw Failure(ConverterFailureCategory.InvalidInput,
                "The converter input does not exist or is empty.", stopwatch);
        }

        return fullPath;
    }

    private static void EnsureAvailable(string path, Stopwatch stopwatch)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw Failure(
                ConverterFailureCategory.FileSystem,
                "A unique converter staging path is already occupied.",
                stopwatch);
        }
    }

    private static void RequireOutput(
        string outputPath,
        ConverterDiagnostics diagnostics,
        Stopwatch stopwatch)
    {
        var information = new FileInfo(outputPath);
        if (!information.Exists)
        {
            throw new CdbConversionException(new ConversionFailure(
                ConverterFailureCategory.MissingOutput,
                "The SQLite exporter completed without creating its expected output.",
                diagnostics with { Duration = stopwatch.Elapsed }));
        }

        if (information.Length == 0)
        {
            TryDelete(outputPath);
            throw new CdbConversionException(new ConversionFailure(
                ConverterFailureCategory.EmptyOutput,
                "The SQLite exporter created an empty output file.",
                diagnostics with { Duration = stopwatch.Elapsed }));
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan requested)
    {
        if (requested <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requested), "The converter timeout must be positive.");
        }

        return requested <= MaximumTimeout ? requested : MaximumTimeout;
    }

    private static CdbConversionException Failure(
        ConverterFailureCategory category,
        string message,
        Stopwatch stopwatch,
        ConverterDiagnostics? diagnostics = null,
        Exception? innerException = null) =>
        new(new ConversionFailure(
            category,
            message,
            diagnostics ?? new ConverterDiagnostics(null, string.Empty, string.Empty, stopwatch.Elapsed)),
            innerException);

    private static void TryDelete(string path)
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
            // Unique staging remnants are recoverable and never replace the user's database.
        }
        catch (UnauthorizedAccessException)
        {
            // Unique staging remnants are recoverable and never replace the user's database.
        }
    }
}
