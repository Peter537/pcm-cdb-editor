using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace PcmCdbEditor.Infrastructure.Internal;

internal static class AtomicJsonFile
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<T> ReadOrCreateAsync<T>(
        string path,
        Func<T> defaultFactory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(defaultFactory);

        var fullPath = Path.GetFullPath(path);
        var gate = Gates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadOrCreateCoreAsync(fullPath, defaultFactory, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var gate = Gates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteCoreAsync(fullPath, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task UpdateAsync<T>(
        string path,
        Func<T> defaultFactory,
        Func<T, T> update,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(defaultFactory);
        ArgumentNullException.ThrowIfNull(update);

        var fullPath = Path.GetFullPath(path);
        var gate = Gates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            T current = await ReadOrCreateCoreAsync(fullPath, defaultFactory, cancellationToken)
                .ConfigureAwait(false);
            await WriteCoreAsync(fullPath, update(current), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<T> ReadOrCreateCoreAsync<T>(
        string fullPath,
        Func<T> defaultFactory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
        {
            return defaultFactory();
        }

        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? defaultFactory();
        }
        catch (JsonException)
        {
            PreserveCorruptFile(fullPath);
            return defaultFactory();
        }
    }

    private static async Task WriteCoreAsync<T>(
        string fullPath,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The settings path must have a parent directory.", nameof(fullPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void PreserveCorruptFile(string path)
    {
        var corruptPath = string.Create(
            CultureInfo.InvariantCulture,
            $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        try
        {
            File.Move(path, corruptPath);
        }
        catch (IOException)
        {
            // Recovery must still return defaults when another process races this move.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only settings file is recoverable in memory even if it cannot be preserved.
        }
    }

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
            // Best-effort cleanup; the unique temporary file cannot replace user state.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}
