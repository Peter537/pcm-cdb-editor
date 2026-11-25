using Microsoft.Data.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

internal sealed class SqliteTestDatabase : IAsyncDisposable
{
    private SqliteTestDatabase(string directory, string path)
    {
        Directory = directory;
        Path = path;
    }

    public string Directory { get; }

    public string Path { get; }

    public static Task<SqliteTestDatabase> CreateAsync(params string[] statements) =>
        CreateAsync([], statements);

    public static Task<SqliteTestDatabase> CreateWithTransactionalSchemaAsync(
        string[] schema, params string[] statements) => CreateAsync(schema, statements);

    private static async Task<SqliteTestDatabase> CreateAsync(string[] schema, string[] statements)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "PcmCdbEditorTests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "fixture.sqlite");
        var database = new SqliteTestDatabase(directory, path);
        try
        {
            await using var connection = Open(path);
            await connection.OpenAsync().ConfigureAwait(false);
            if (schema.Length != 0)
            {
                using var transaction = connection.BeginTransaction();
                await ExecuteStatementsAsync(connection, schema, transaction).ConfigureAwait(false);
                transaction.Commit();
            }

            await ExecuteStatementsAsync(connection, statements, transaction: null).ConfigureAwait(false);
            return database;
        }
        catch (Exception setupFailure)
        {
            try
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException cleanupFailure)
            {
                throw new AggregateException("SQLite fixture setup and cleanup both failed.", setupFailure, cleanupFailure);
            }

            throw;
        }
    }

    private static async Task ExecuteStatementsAsync(
        SqliteConnection connection, IEnumerable<string> statements, SqliteTransaction? transaction)
    {
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    public async Task ExecuteAsync(string statement)
    {
        await using var connection = Open(Path);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<T> ScalarAsync<T>(string statement)
    {
        await using var connection = Open(Path);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        return (T)Convert.ChangeType(
            await command.ExecuteScalarAsync().ConfigureAwait(false) ?? throw new InvalidDataException(),
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not remove SQLite fixture '{Directory}'.", exception);
            }
        }
    }

    private static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
        ForeignKeys = true
    }.ToString());
}
