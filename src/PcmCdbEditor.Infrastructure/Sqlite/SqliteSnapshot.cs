using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace PcmCdbEditor.Infrastructure.Sqlite;

internal static class SqliteSnapshot
{
    public static async Task CreateAsync(
        string sourcePath, string destinationPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            await SqliteOperationRunner.RunAsync(
                () => CopyAsync(sourcePath, destinationPath, operation.Token), operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The SQLite snapshot could not finish within the conversion deadline.");
        }
    }

    private static async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = SqliteSupport.CreateConnection(sourcePath, SqliteOpenMode.ReadOnly);
        await using var destination = SqliteSupport.CreateConnection(destinationPath);
        using var sourceInterrupt = await SqliteOperationRunner.OpenInterruptiblyAsync(source, cancellationToken)
            .ConfigureAwait(false);
        using var destinationInterrupt = await SqliteOperationRunner.OpenInterruptiblyAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        using var backup = raw.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main");
        if (backup.IsInvalid)
        {
            throw new SqliteException("Could not initialize the working database snapshot.", raw.sqlite3_errcode(destination.Handle));
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int result = raw.sqlite3_backup_step(backup, 128);
            switch (result)
            {
                case raw.SQLITE_DONE:
                    cancellationToken.ThrowIfCancellationRequested();
                    return;
                case raw.SQLITE_OK:
                    break;
                case raw.SQLITE_BUSY:
                case raw.SQLITE_LOCKED:
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new SqliteException("Could not copy the working database snapshot.", result);
            }
        }
    }
}
