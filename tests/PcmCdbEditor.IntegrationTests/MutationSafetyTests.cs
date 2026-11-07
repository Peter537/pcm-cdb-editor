using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.History;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class MutationSafetyTests
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

        UnsafeRowMutationException exception = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
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

        UnsafeRowMutationException exception = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
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

        UnsafeRowMutationException exception = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
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
            """CREATE TABLE data (ID INTEGER PRIMARY KEY, value TEXT NOT NULL, "AUTOINCREMENT" TEXT DEFAULT 'AUTOINCREMENT' /* AUTOINCREMENT */)""",
            "CREATE TABLE update_log (data_ID INTEGER NOT NULL)",
            "CREATE TRIGGER data_update AFTER UPDATE ON data BEGIN DELETE FROM update_log WHERE data_ID = NEW.ID; END",
            "INSERT INTO data(ID, value) VALUES (1, 'target')").ConfigureAwait(false);
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

    [TestMethod]
    public async Task AutoincrementPreflightPreservesPreviousSequenceAndAllowsOrdinaryUpdates()
    {
        await using var database = await CreateAutoincrementDatabaseAsync().ConfigureAwait(false);
        var (catalog, row) = await ReadSingleRowAsync(database, "data").ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var insert = new RowInsertionOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, []);
        var delete = new RowDeletionOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, row);
        var insertError = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(
            () => store.InsertRowAsync(database.Path, catalog, insert, CancellationToken.None)).ConfigureAwait(false);
        var deleteError = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(
            () => store.DeleteRowAsync(database.Path, catalog, delete, CancellationToken.None)).ConfigureAwait(false);
        StringAssert.Contains(insertError.Message, "AUTOINCREMENT", StringComparison.Ordinal);
        StringAssert.Contains(deleteError.Message, "sqlite_sequence", StringComparison.Ordinal);

        var update = new CellUpdateOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow,
            row.Identity!, "value", row.Values["value"], SqliteValue.Text("changed"), row.Revision);
        var updated = await store.UpdateCellAsync(database.Path, catalog, update, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SqliteValue.Text("changed"), updated.CurrentRow!.Values["value"]);
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM data").ConfigureAwait(false));
        await AssertNextAutoincrementIdAsync(database).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(EditReplayDirection.Undo)]
    [DataRow(EditReplayDirection.Redo)]
    public async Task AutoincrementReplayLeavesRowsAndFutureIdsUntouched(EditReplayDirection direction)
    {
        await using var database = await CreateAutoincrementDatabaseAsync().ConfigureAwait(false);
        var (catalog, row) = await ReadSingleRowAsync(database, "data").ConfigureAwait(false);
        var deletion = new RowDeletionOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, row);
        RowReplayGuard guard = RowReplayGuard.Present("data", row);
        if (direction == EditReplayDirection.Undo)
        {
            await database.ExecuteAsync("DELETE FROM data WHERE ID = 1").ConfigureAwait(false);
            guard = RowReplayGuard.Absent("data", row.Identity!);
        }

        var replay = new EditHistoryReplay(deletion, direction, [guard]);
        var error = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
            new SqliteEditOperationReplayer().ReplayAsync(database.Path, catalog, replay, CancellationToken.None))
            .ConfigureAwait(false);
        StringAssert.Contains(error.Message, "AUTOINCREMENT", StringComparison.Ordinal);
        Assert.AreEqual(direction == EditReplayDirection.Undo ? 0L : 1L,
            await database.ScalarAsync<long>("SELECT COUNT(*) FROM data").ConfigureAwait(false));
        await AssertNextAutoincrementIdAsync(database).ConfigureAwait(false);
    }

    private static Task<SqliteTestDatabase> CreateAutoincrementDatabaseAsync() =>
        SqliteTestDatabase.CreateAsync(
            "CREATE TABLE data (ID INTEGER PRIMARY KEY -- comment with a CR\rand a quote '\n\uFEFFaUtoIncrement, value TEXT)",
            "INSERT INTO data VALUES (1, 'original'), (10, 'retired')",
            "DELETE FROM data WHERE ID = 10");

    private static async Task AssertNextAutoincrementIdAsync(SqliteTestDatabase database)
    {
        Assert.AreEqual(10L, await database.ScalarAsync<long>(
            "SELECT seq FROM sqlite_sequence WHERE name = 'data'").ConfigureAwait(false));
        Assert.AreEqual(11L, await database.ScalarAsync<long>(
            "INSERT INTO data DEFAULT VALUES RETURNING ID").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("INSERT")]
    [DataRow("DELETE")]
    public async Task InsertAndDeleteRequireBothDirectionsToBeTriggerSafe(string triggerEvent)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE parent (ID INTEGER PRIMARY KEY)",
            "INSERT INTO parent VALUES (1)",
            "CREATE TABLE audit (entry INTEGER)").ConfigureAwait(false);
        var (catalog, row) = await ReadSingleRowAsync(database, "parent").ConfigureAwait(false);
        await database.ExecuteAsync(
            $"CREATE TRIGGER \"UPDATE\" /* UPDATE */ AFTER {triggerEvent} ON parent BEGIN INSERT INTO audit VALUES (1); END")
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var insertion = new RowInsertionOperation(Guid.NewGuid(), "parent", DateTimeOffset.UtcNow, []);
        var deletion = new RowDeletionOperation(Guid.NewGuid(), "parent", DateTimeOffset.UtcNow, row);

        var insertError = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(
            () => store.InsertRowAsync(database.Path, catalog, insertion, CancellationToken.None)).ConfigureAwait(false);
        var deleteError = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(
            () => store.DeleteRowAsync(database.Path, catalog, deletion, CancellationToken.None)).ConfigureAwait(false);

        StringAssert.Contains(insertError.Message, $"{triggerEvent} trigger", StringComparison.Ordinal);
        StringAssert.Contains(deleteError.Message, $"{triggerEvent} trigger", StringComparison.Ordinal);
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM parent WHERE ID=1").ConfigureAwait(false));
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM parent").ConfigureAwait(false));
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM audit").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("CASCADE")]
    [DataRow("SET NULL")]
    [DataRow("SET DEFAULT")]
    public async Task UpdateActionsBlockChangesToTheReferencedKeyAndPermitOtherEdits(string action)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE parent (ID INTEGER PRIMARY KEY, code TEXT UNIQUE, note TEXT)",
            $"CREATE TABLE child (code TEXT REFERENCES parent(code) ON UPDATE {action})",
            "INSERT INTO parent VALUES (1, 'A', 'old')",
            "INSERT INTO child VALUES ('A')").ConfigureAwait(false);
        var (catalog, row) = await ReadSingleRowAsync(database, "parent").ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var update = new RowUpdateOperation(Guid.NewGuid(), "parent", DateTimeOffset.UtcNow, row.Identity!,
            [KeyValuePair.Create("code", row.Values["code"])],
            [KeyValuePair.Create("code", SqliteValue.Text("B"))], row.Revision);
        var exception = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(
            () => store.UpdateRowAsync(database.Path, catalog, update, CancellationToken.None)).ConfigureAwait(false);
        StringAssert.Contains(exception.Message, $"ON UPDATE {action}", StringComparison.Ordinal);

        var note = new CellUpdateOperation(Guid.NewGuid(), "parent", DateTimeOffset.UtcNow, row.Identity!,
            "note", row.Values["note"], SqliteValue.Text("new"), row.Revision);
        var result = await store.UpdateCellAsync(database.Path, catalog, note, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SqliteValue.Text("new"), result.CurrentRow!.Values["note"]);
        Assert.AreEqual("A", await database.ScalarAsync<string>("SELECT code FROM parent").ConfigureAwait(false));
        Assert.AreEqual("A", await database.ScalarAsync<string>("SELECT code FROM child").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task GeneratedParentKeyIsCheckedWhenItsInputChanges()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE parent (ID INTEGER PRIMARY KEY, value TEXT, code TEXT AS (upper(value)) STORED UNIQUE)",
            "CREATE TABLE child (code TEXT REFERENCES parent(code) ON UPDATE CASCADE)",
            "INSERT INTO parent(ID, value) VALUES (1, 'a')",
            "INSERT INTO child VALUES ('A')").ConfigureAwait(false);
        var (catalog, row) = await ReadSingleRowAsync(database, "parent").ConfigureAwait(false);
        var update = new CellUpdateOperation(Guid.NewGuid(), "parent", DateTimeOffset.UtcNow, row.Identity!,
            "value", row.Values["value"], SqliteValue.Text("b"), row.Revision);
        var error = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
            new SqliteTableDataStore().UpdateCellAsync(database.Path, catalog, update, CancellationToken.None))
            .ConfigureAwait(false);
        StringAssert.Contains(error.Message, "ON UPDATE CASCADE", StringComparison.Ordinal);
        Assert.AreEqual("a", await database.ScalarAsync<string>("SELECT value FROM parent").ConfigureAwait(false));
        Assert.AreEqual("A", await database.ScalarAsync<string>("SELECT code FROM child").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(EditReplayDirection.Undo, false)]
    [DataRow(EditReplayDirection.Redo, false)]
    [DataRow(EditReplayDirection.Undo, true)]
    [DataRow(EditReplayDirection.Redo, true)]
    public async Task GenericAndReplayUpdatesRecheckLiveSchema(EditReplayDirection direction, bool useTrigger)
    {
        string current = direction == EditReplayDirection.Undo ? "B" : "A";
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE data (ID INTEGER PRIMARY KEY, code TEXT UNIQUE)",
            $"INSERT INTO data VALUES (1, '{current}')",
            "CREATE TABLE audit (entry INTEGER)").ConfigureAwait(false);
        var (catalog, row) = await ReadSingleRowAsync(database, "data").ConfigureAwait(false);
        await database.ExecuteAsync(useTrigger
            ? "CREATE TRIGGER notify_AFTER_UPDATE AFTER UPDATE OF code ON data BEGIN INSERT INTO audit VALUES (1); END"
            : $"CREATE TABLE child (code TEXT REFERENCES data(code) ON UPDATE CASCADE); INSERT INTO child VALUES ('{current}')")
            .ConfigureAwait(false);
        var update = new CellUpdateOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, row.Identity!,
            "code", SqliteValue.Text("A"), SqliteValue.Text("B"), row.Revision);
        var replay = new EditHistoryReplay(update, direction, [RowReplayGuard.Present("data", row)]);
        string reason = useTrigger ? "UPDATE trigger" : "ON UPDATE CASCADE";
        var directError = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
            new SqliteTableDataStore().UpdateCellAsync(database.Path, catalog, update, CancellationToken.None))
            .ConfigureAwait(false);
        var replayError = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
            new SqliteEditOperationReplayer().ReplayAsync(database.Path, catalog, replay, CancellationToken.None))
            .ConfigureAwait(false);

        StringAssert.Contains(directError.Message, reason, StringComparison.Ordinal);
        StringAssert.Contains(replayError.Message, reason, StringComparison.Ordinal);
        Assert.AreEqual(current, await database.ScalarAsync<string>("SELECT code FROM data").ConfigureAwait(false));
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM audit").ConfigureAwait(false));
        if (!useTrigger)
        {
            Assert.AreEqual(current, await database.ScalarAsync<string>("SELECT code FROM child").ConfigureAwait(false));
        }
    }

    [TestMethod]
    public async Task SchemaReplacePolicyCannotSilentlyRemoveAnotherRow()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE data (ID INTEGER PRIMARY KEY, code TEXT UNIQUE ON CONFLICT REPLACE)",
            "INSERT INTO data VALUES (1, 'A'), (2, 'B')").ConfigureAwait(false);
        var (catalog, row) = await ReadSingleRowAsync(database, "data",
            new FilterCondition("ID", FilterOperator.Equals, SqliteValue.Integer(1))).ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var insertion = new RowInsertionOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow,
            [KeyValuePair.Create("code", SqliteValue.Text("B"))]);
        var update = new CellUpdateOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, row.Identity!,
            "code", row.Values["code"], SqliteValue.Text("B"), row.Revision);
        await Assert.ThrowsExactlyAsync<SqliteException>(
            () => store.InsertRowAsync(database.Path, catalog, insertion, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(
            () => store.UpdateCellAsync(database.Path, catalog, update, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("A", await database.ScalarAsync<string>("SELECT code FROM data WHERE ID=1").ConfigureAwait(false));
        Assert.AreEqual("B", await database.ScalarAsync<string>("SELECT code FROM data WHERE ID=2").ConfigureAwait(false));
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
