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

    public static async Task<SqliteTestDatabase> CreateAsync(params string[] statements)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "PcmCdbEditorTests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "fixture.sqlite");
        await using var connection = Open(path);
        await connection.OpenAsync().ConfigureAwait(false);
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return new SqliteTestDatabase(directory, path);
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
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A failed test still leaves only an isolated temp fixture.
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
