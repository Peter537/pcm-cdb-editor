namespace PcmCdbEditor.App;

public sealed partial class MainWindow
{
    private async Task RunSessionReaderAsync(
        Func<CancellationToken, Task> read, CancellationToken cancellationToken)
    {
        if (_sessionReadLifetime is not { } lifetime)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await TrackSessionReaderAsync(read(linked.Token));
    }

    private async Task TrackSessionReaderAsync(Task read)
    {
        _sessionReaders.Add(read);
        try
        {
            await read;
        }
        finally
        {
            _sessionReaders.Remove(read);
        }
    }

    private async Task<T> TrackSessionReaderAsync<T>(Task<T> read)
    {
        await TrackSessionReaderAsync((Task)read);
        return await read;
    }

    private async Task DrainSessionReadersAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource? lifetime = _sessionReadLifetime;
        _sessionReadLifetime = null;
        lifetime?.Cancel();
        try
        {
            Task[] readers = _sessionReaders.ToArray();
            await Task.WhenAll(readers.Select(ObserveReaderCompletionAsync))
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new IOException("Working database reads are still stopping. Try closing the session again.", exception);
        }
    }

    private static async Task ObserveReaderCompletionAsync(Task reader) =>
        await reader.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}
