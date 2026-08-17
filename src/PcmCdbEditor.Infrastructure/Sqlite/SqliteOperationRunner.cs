using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace PcmCdbEditor.Infrastructure.Sqlite;

/// <summary>
/// Keeps Microsoft.Data.Sqlite's synchronous native work off UI dispatchers and
/// turns an in-flight SQLite interrupt into the cancellation requested by the caller.
/// </summary>
internal static class SqliteOperationRunner
{
    private static readonly AsyncLocal<int> ExecutionDepth = new();

    public static Task RunAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return ExecutionDepth.Value > 0
            ? ExecuteAsync(operation, cancellationToken)
            : Task.Run(
                async () =>
                {
                    int previousDepth = ExecutionDepth.Value;
                    ExecutionDepth.Value = previousDepth + 1;
                    try
                    {
                        await ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        ExecutionDepth.Value = previousDepth;
                    }
                },
                CancellationToken.None);
    }

    public static Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        return ExecutionDepth.Value > 0
            ? ExecuteAsync(operation, cancellationToken)
            : Task.Run(
                async () =>
                {
                    int previousDepth = ExecutionDepth.Value;
                    ExecutionDepth.Value = previousDepth + 1;
                    try
                    {
                        return await ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        ExecutionDepth.Value = previousDepth;
                    }
                },
                CancellationToken.None);
    }

    public static async Task<CancellationTokenRegistration> OpenInterruptiblyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return RegisterInterrupt(connection, cancellationToken);
    }

    internal static CancellationTokenRegistration RegisterInterrupt(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!cancellationToken.CanBeCanceled)
        {
            return default;
        }

        return cancellationToken.Register(
            static state =>
            {
                var target = (SqliteConnection)state!;
                try
                {
                    raw.sqlite3_interrupt(target.Handle);
                }
                catch (ObjectDisposedException)
                {
                    // Registration disposal precedes connection disposal. This guard only
                    // covers a concurrent owner teardown already in progress.
                }
                catch (InvalidOperationException)
                {
                    // A connection canceled between construction and opening has no handle.
                }
            },
            connection,
            useSynchronizationContext: false);
    }

    internal static async Task RollbackAfterFailureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException) when (IsInAutocommitMode(connection))
        {
            // SQLite can roll back an entire write transaction when sqlite3_interrupt
            // stops the statement. The provider still needs the rollback attempt to
            // complete its transaction object, but "no transaction is active" must
            // not replace the operation failure that led here.
        }
        catch (InvalidOperationException) when (IsInAutocommitMode(connection))
        {
            // A provider-side completed transaction is equivalent to the native
            // autocommit state for this best-effort cleanup path.
        }
    }

    private static async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation().ConfigureAwait(false);
        }
        catch (SqliteException exception) when (
            cancellationToken.IsCancellationRequested &&
            exception.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            throw new OperationCanceledException(
                "The SQLite operation was canceled.",
                exception,
                cancellationToken);
        }
    }

    private static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        }
        catch (SqliteException exception) when (
            cancellationToken.IsCancellationRequested &&
            exception.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            throw new OperationCanceledException(
                "The SQLite operation was canceled.",
                exception,
                cancellationToken);
        }
    }

    private static bool IsInAutocommitMode(SqliteConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return true;
        }

        try
        {
            return raw.sqlite3_get_autocommit(connection.Handle) != 0;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
