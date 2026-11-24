using System.Text;

namespace PcmCdbEditor.Infrastructure.Internal;

internal sealed record ProcessExecutionResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class BoundedProcessTimeoutException(
    string message,
    string standardOutput,
    string standardError) : TimeoutException(message)
{
    public string StandardOutput { get; } = standardOutput;

    public string StandardError { get; } = standardError;
}

internal static class BoundedProcessRunner
{
    internal const int DiagnosticCharacterLimit = 16 * 1024;

    public static async Task<ProcessExecutionResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyCollection<string> sensitiveValues,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(sensitiveValues);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The process timeout must be positive.");
        }

        using var process = WindowsConverterProcess.Start(executablePath, arguments);
        var sensitive = sensitiveValues
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static item => item.Length)
            .ToArray();
        using var readerCancellation = new CancellationTokenSource();
        var outputTask = ReadBoundedAndSanitizedAsync(process.StandardOutput, sensitive, readerCancellation.Token);
        var errorTask = ReadBoundedAndSanitizedAsync(process.StandardError, sensitive, readerCancellation.Token);
        var completion = Task.WhenAll(process.WaitForExitAsync(readerCancellation.Token), outputTask, errorTask);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await completion.WaitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            process.Stop();

            readerCancellation.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or IOException)
            {
                if (!completion.IsCompleted)
                {
                    _ = completion.ContinueWith(static task => _ = task.Exception,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new BoundedProcessTimeoutException(
                $"The converter did not finish within {timeout.TotalMinutes:0.##} minutes.",
                outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty,
                errorTask.IsCompletedSuccessfully ? errorTask.Result : string.Empty);
        }

        return new ProcessExecutionResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private static async Task<string> ReadBoundedAndSanitizedAsync(
        StreamReader reader,
        string[] sensitiveValues,
        CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var builder = new StringBuilder(Math.Min(DiagnosticCharacterLimit, 4096));
        var wasTruncated = false;
        var maximumSensitiveLength = sensitiveValues.Length == 0
            ? 1
            : sensitiveValues.Max(static value => value.Length);
        var carry = string.Empty;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var combined = carry + new string(buffer, 0, read);
            var safeLength = Math.Max(0, combined.Length - maximumSensitiveLength + 1);
            var consumed = AppendSanitized(
                combined,
                safeLength,
                isFinal: false,
                sensitiveValues,
                builder,
                ref wasTruncated);
            carry = combined[consumed..];
        }

        _ = AppendSanitized(
            carry,
            carry.Length,
            isFinal: true,
            sensitiveValues,
            builder,
            ref wasTruncated);
        if (wasTruncated)
        {
            builder.Append("\n[diagnostic output truncated]");
        }

        return builder.ToString().Trim();
    }

    private static int AppendSanitized(
        string value,
        int safeLength,
        bool isFinal,
        string[] sensitiveValues,
        StringBuilder destination,
        ref bool wasTruncated)
    {
        var index = 0;
        while (index < safeLength)
        {
            var match = FindSensitiveMatch(value, index, sensitiveValues);
            if (match is not null)
            {
                AppendBounded(destination, "[path]", ref wasTruncated);
                index += match.Length;
                continue;
            }

            if (isFinal && IsSensitivePrefix(value, index, sensitiveValues))
            {
                AppendBounded(destination, "[path]", ref wasTruncated);
                return value.Length;
            }

            var character = value[index];
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character))
            {
                AppendBounded(destination, character, ref wasTruncated);
            }

            index++;
        }

        return index;
    }

    private static string? FindSensitiveMatch(
        string value,
        int index,
        string[] sensitiveValues)
    {
        var remaining = value.AsSpan(index);
        foreach (var sensitiveValue in sensitiveValues)
        {
            if (remaining.StartsWith(sensitiveValue.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return sensitiveValue;
            }
        }

        return null;
    }

    private static bool IsSensitivePrefix(
        string value,
        int index,
        string[] sensitiveValues)
    {
        var remaining = value.AsSpan(index);
        foreach (var sensitiveValue in sensitiveValues)
        {
            if (remaining.Length < sensitiveValue.Length
                && sensitiveValue.AsSpan().StartsWith(remaining, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendBounded(
        StringBuilder destination,
        char value,
        ref bool wasTruncated)
    {
        if (destination.Length < DiagnosticCharacterLimit)
        {
            destination.Append(value);
        }
        else
        {
            wasTruncated = true;
        }
    }

    private static void AppendBounded(
        StringBuilder destination,
        string value,
        ref bool wasTruncated)
    {
        var remaining = DiagnosticCharacterLimit - destination.Length;
        if (remaining > 0)
        {
            destination.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
        }

        wasTruncated |= value.Length > remaining;
    }
}
