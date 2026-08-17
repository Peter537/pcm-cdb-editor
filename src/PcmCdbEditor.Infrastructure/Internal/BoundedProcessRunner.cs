using System.Diagnostics;
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

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The converter process could not be started.");
        }

        var sensitive = sensitiveValues
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static item => item.Length)
            .ToArray();
        var outputTask = ReadBoundedAndSanitizedAsync(process.StandardOutput, sensitive);
        var errorTask = ReadBoundedAndSanitizedAsync(process.StandardError, sensitive);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            var diagnostics = await DrainReadersAsync(outputTask, errorTask).ConfigureAwait(false);
            throw new BoundedProcessTimeoutException(
                $"The converter did not finish within {timeout.TotalMinutes:0.##} minutes.",
                diagnostics.StandardOutput,
                diagnostics.StandardError);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await DrainReadersAsync(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return new ProcessExecutionResult(
            process.ExitCode,
            output,
            error);
    }

    private static async Task<string> ReadBoundedAndSanitizedAsync(
        StreamReader reader,
        string[] sensitiveValues)
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
            var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
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

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the kill request.
        }
    }

    private static async Task<(string StandardOutput, string StandardError)> DrainReadersAsync(
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return (await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The caller receives the timeout or cancellation exception from the process wait.
            return (string.Empty, string.Empty);
        }
    }
}
