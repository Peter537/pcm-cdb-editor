using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.History;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class DeleteSafetyTests
{
    [TestMethod]
    [DataRow("CASCADE")]
    [DataRow("SET NULL")]
    [DataRow("SET DEFAULT")]
    public async Task SideEffectingInboundForeignKeysRejectRowDeletion(string deleteAction)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE parent (ID INTEGER PRIMARY KEY, value TEXT NOT NULL)",
            $"CREATE TABLE child (ID INTEGER PRIMARY KEY, parent_ID INTEGER DEFAULT 0 REFERENCES parent(ID) ON DELETE {deleteAction})",
            "INSERT INTO parent VALUES (0, 'default-parent'), (1, 'target')",
            "INSERT INTO child VALUES (10, 1)").ConfigureAwait(false);
        (DatabaseSchemaCatalog catalog, TypedRow row) = await ReadSingleRowAsync(
                database,
                "parent",
                new FilterCondition("ID", FilterOperator.Equals, SqliteValue.Integer(1)))
            .ConfigureAwait(false);
        var operation = new RowDeletionOperation(Guid.NewGuid(), "parent", DateTimeOffset.UtcNow, row);

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                new SqliteTableDataStore().DeleteRowAsync(
                    database.Path,
                    catalog,
                    operation,
                    CancellationToken.None))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Message, $"ON DELETE {deleteAction}", StringComparison.Ordinal);
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM parent WHERE ID = 1").ConfigureAwait(false));
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT parent_ID FROM child WHERE ID = 10").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SideEffectingInboundForeignKeyRejectsRedoBeforeMutation()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE parent (ID INTEGER PRIMARY KEY, value TEXT NOT NULL)",
            "CREATE TABLE child (ID INTEGER PRIMARY KEY, parent_ID INTEGER REFERENCES parent(ID) ON DELETE CASCADE)",
            "INSERT INTO parent VALUES (1, 'target')",
            "INSERT INTO child VALUES (10, 1)").ConfigureAwait(false);
        (DatabaseSchemaCatalog catalog, TypedRow row) = await ReadSingleRowAsync(database, "parent")
            .ConfigureAwait(false);
        var operation = new RowDeletionOperation(Guid.NewGuid(), "parent", DateTimeOffset.UtcNow, row);
        var replay = new EditHistoryReplay(
            operation,
            EditReplayDirection.Redo,
            [RowReplayGuard.Present("parent", row)]);

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                new SqliteEditOperationReplayer().ReplayAsync(
                    database.Path,
                    catalog,
                    replay,
                    CancellationToken.None))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Message, "ON DELETE CASCADE", StringComparison.Ordinal);
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM parent").ConfigureAwait(false));
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM child").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DeleteTriggerRejectsRowDeletionBeforeItsSideEffect()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE parent (ID INTEGER PRIMARY KEY, value TEXT NOT NULL)",
            "CREATE TABLE delete_log (parent_ID INTEGER NOT NULL)",
            "CREATE TRIGGER parent_delete AFTER DELETE ON parent BEGIN INSERT INTO delete_log VALUES (OLD.ID); END",
            "INSERT INTO parent VALUES (1, 'target')").ConfigureAwait(false);
        (DatabaseSchemaCatalog catalog, TypedRow row) = await ReadSingleRowAsync(database, "parent")
            .ConfigureAwait(false);

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                new SqliteTableDataStore().DeleteRowAsync(
                    database.Path,
                    catalog,
                    new RowDeletionOperation(Guid.NewGuid(), "parent", DateTimeOffset.UtcNow, row),
                    CancellationToken.None))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Message, "DELETE trigger", StringComparison.Ordinal);
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM parent").ConfigureAwait(false));
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM delete_log").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("NO ACTION")]
    [DataRow("RESTRICT")]
    public async Task RestrictiveForeignKeyRemainsEnforcedBySqlite(string deleteAction)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE parent (ID INTEGER PRIMARY KEY, value TEXT NOT NULL)",
            $"CREATE TABLE child (ID INTEGER PRIMARY KEY, parent_ID INTEGER REFERENCES parent(ID) ON DELETE {deleteAction})",
            "INSERT INTO parent VALUES (1, 'target')",
            "INSERT INTO child VALUES (10, 1)").ConfigureAwait(false);
        (DatabaseSchemaCatalog catalog, TypedRow row) = await ReadSingleRowAsync(database, "parent")
            .ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                new SqliteTableDataStore().DeleteRowAsync(
                    database.Path,
                    catalog,
                    new RowDeletionOperation(Guid.NewGuid(), "parent", DateTimeOffset.UtcNow, row),
                    CancellationToken.None))
            .ConfigureAwait(false);

        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM parent").ConfigureAwait(false));
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM child").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task OrdinaryDeleteUndoAndRedoRemainLossless()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE data (ID INTEGER PRIMARY KEY, value TEXT NOT NULL)",
            "CREATE TABLE update_log (data_ID INTEGER NOT NULL)",
            "CREATE TRIGGER data_update AFTER UPDATE ON data BEGIN DELETE FROM update_log WHERE data_ID = NEW.ID; END",
            "INSERT INTO data VALUES (1, 'target')").ConfigureAwait(false);
        (DatabaseSchemaCatalog catalog, TypedRow row) = await ReadSingleRowAsync(database, "data")
            .ConfigureAwait(false);
        var operation = new RowDeletionOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, row);
        var store = new SqliteTableDataStore();
        var replayer = new SqliteEditOperationReplayer();

        EditResult deleted = await store.DeleteRowAsync(
                database.Path,
                catalog,
                operation,
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM data").ConfigureAwait(false));

        EditReplayResult undone = await replayer.ReplayAsync(
                database.Path,
                catalog,
                new EditHistoryReplay(
                    operation,
                    EditReplayDirection.Undo,
                    [RowReplayGuard.Absent("data", deleted.CurrentRow!.Identity!)]),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM data").ConfigureAwait(false));

        await replayer.ReplayAsync(
                database.Path,
                catalog,
                new EditHistoryReplay(operation, EditReplayDirection.Redo, undone.OppositeGuards),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM data").ConfigureAwait(false));
    }

    private static async Task<(DatabaseSchemaCatalog Catalog, TypedRow Row)> ReadSingleRowAsync(
        SqliteTestDatabase database,
        string tableName,
        FilterExpression? filter = null)
    {
        DatabaseSchemaCatalog catalog = await new SqliteTableCatalog()
            .DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        TablePage page = await new SqliteTableDataStore()
            .QueryAsync(
                database.Path,
                catalog,
                new TableQuery(tableName, new PageRequest(0, 10), filter: filter),
                CancellationToken.None)
            .ConfigureAwait(false);
        return (catalog, page.Rows.Single());
    }
}
