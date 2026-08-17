using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.History;
using PcmCdbEditor.Infrastructure.Settings;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class HistoryAndSettingsTests
{
    [TestMethod]
    public void EditHistoryAtomicallyReloadsFullTypedUndoAndRedoStacks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.json");
        try
        {
            var history = new EditHistory(path);
            var row = new TypedRow(
                RowIdentity.FromPrimaryKey([new RowIdentityComponent("ID", SqliteValue.Integer(7))]),
                new Dictionary<string, SqliteValue>
                {
                    ["ID"] = SqliteValue.Integer(7),
                    ["value"] = SqliteValue.Text("payload"),
                    ["blob"] = SqliteValue.Blob([0, 1, 2])
                });
            var delete = new RowDeletionOperation(Guid.NewGuid(), "DYN_data", DateTimeOffset.UtcNow, row);
            var insert = new RowInsertionOperation(
                Guid.NewGuid(), "DYN_data", DateTimeOffset.UtcNow, row.Values, row.Identity!);
            history.Record(delete);
            history.Record(insert);
            var pending = history.TakeUndo();
            history.CompleteUndo(pending);

            var reloaded = new EditHistory(path);
            Assert.AreEqual(1, reloaded.State.UndoCount);
            Assert.AreEqual(1, reloaded.State.RedoCount);
            var restoredDelete = Assert.IsInstanceOfType<RowDeletionOperation>(reloaded.TakeUndo());
            Assert.AreEqual("payload", restoredDelete.DeletedRow.Values["value"].TextValue);
            reloaded.RestoreFailedUndo(restoredDelete);
            var restoredInsert = Assert.IsInstanceOfType<RowInsertionOperation>(reloaded.TakeRedo());
            Assert.AreEqual(SqliteValueKind.Blob, restoredInsert.Values["blob"].Kind);
            Assert.ThrowsExactly<InvalidOperationException>(() => reloaded.Record(delete));
            reloaded.RestoreFailedRedo(restoredInsert);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SettingsAreAtomicNormalizedAndContainNoDatabaseRowContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonSettingsStore(path);
            Assert.AreEqual(100, (await store.LoadPreferencesAsync(CancellationToken.None).ConfigureAwait(false)).PageSize);
            await store.SavePreferencesAsync(
                new EditorPreferences(
                    ApplicationTheme.Dark,
                    GridDensity.Comfortable,
                    pageSize: -1,
                    ForeignKeyDisplayMode.ResolvedName,
                    Enumerable.Range(0, 20).Select(index => $"C:\\neutral\\{index}.cdb")),
                CancellationToken.None).ConfigureAwait(false);
            var preferences = await store.LoadPreferencesAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(100, preferences.PageSize);
            Assert.AreEqual(12, preferences.RecentFiles.Count);
            await store.SaveTableViewStateAsync(
                new TableViewState(
                    "signature", "DYN_table",
                    [new ColumnDisplayState("value", 120, 0, true, false)],
                    [new SortDescriptor("value", SortDirection.Ascending)],
                    GridDensity.Compact,
                    frozenColumnCount: 0),
                CancellationToken.None).ConfigureAwait(false);
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            Assert.IsFalse(json.Contains("database-row-secret", StringComparison.Ordinal));
            Assert.IsFalse(Directory.EnumerateFiles(directory).Any(file => file.EndsWith(".tmp", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CorruptSettingsArePreservedAndRecoveredWithSafeDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json").ConfigureAwait(false);
            var preferences = await new JsonSettingsStore(path)
                .LoadPreferencesAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(ApplicationTheme.System, preferences.Theme);
            Assert.AreEqual(GridDensity.Compact, preferences.Density);
            Assert.AreEqual(100, preferences.PageSize);
            Assert.AreEqual(ForeignKeyDisplayMode.RawAndName, preferences.ForeignKeyDisplayMode);
            Assert.IsFalse(File.Exists(path));
            Assert.HasCount(1, Directory.GetFiles(directory, "settings.json.corrupt-*"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ValidIncompleteSettingsTreatMissingOrNullTableViewStatesAsEmpty(bool includeNullTableViewStates)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var tableViewStatesProperty = includeNullTableViewStates
                ? ",\"TABLEVIEWSTATES\":null"
                : string.Empty;
            await File.WriteAllTextAsync(
                    path,
                    "{\"PREFERENCES\":{\"THEME\":0,\"DENSITY\":0,\"PAGESIZE\":100," +
                    "\"FOREIGNKEYDISPLAYMODE\":2,\"RECENTFILES\":[]}" + tableViewStatesProperty + "}")
                .ConfigureAwait(false);

            var store = new JsonSettingsStore(path);
            Assert.IsNull(await store.LoadTableViewStateAsync(
                    "signature",
                    "DYN_table",
                    CancellationToken.None)
                .ConfigureAwait(false));

            var expected = new TableViewState(
                "signature",
                "DYN_table",
                [new ColumnDisplayState("value", 120, 0, true, false)],
                [new SortDescriptor("value", SortDirection.Ascending)],
                GridDensity.Compact,
                frozenColumnCount: 0);
            await store.SaveTableViewStateAsync(expected, CancellationToken.None).ConfigureAwait(false);

            var actual = await store.LoadTableViewStateAsync(
                    "SIGNATURE",
                    "dyn_TABLE",
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(actual);
            Assert.AreEqual("DYN_table", actual.TableName);
            Assert.HasCount(1, actual.Columns);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow(42, 100)]
    [DataRow(100, 100)]
    [DataRow(250, 250)]
    public async Task PersistedPageSizeIsRestrictedToSupportedUiChoices(int persistedPageSize, int expectedPageSize)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                    path,
                    "{\"PREFERENCES\":{\"THEME\":0,\"DENSITY\":0,\"PAGESIZE\":" + persistedPageSize +
                    ",\"FOREIGNKEYDISPLAYMODE\":2,\"RECENTFILES\":[]},\"TABLEVIEWSTATES\":{}}")
                .ConfigureAwait(false);

            var preferences = await new JsonSettingsStore(path)
                .LoadPreferencesAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(expectedPageSize, preferences.PageSize);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DiskHistoryReplaysUpdateInsertAndDeleteWithSavedBaselineAndTypedRows()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE data (ID INTEGER PRIMARY KEY, value TEXT, optional TEXT NULL, payload BLOB)",
            "INSERT INTO data VALUES (1, 'old', NULL, X'000102')").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog()
            .DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var replayer = new SqliteEditOperationReplayer();
        var history = new EditHistory(Path.Combine(database.Directory, "edit-history.json"));

        TypedRow original = (await store.QueryAsync(
                database.Path,
                catalog,
                new TableQuery("data", new PageRequest(0, 10)),
                CancellationToken.None)
            .ConfigureAwait(false)).Rows.Single();
        var update = new RowUpdateOperation(
            Guid.NewGuid(),
            "data",
            DateTimeOffset.UtcNow,
            original.Identity!,
            [KeyValuePair.Create("value", original.Values["value"])],
            [KeyValuePair.Create("value", SqliteValue.Text("new"))],
            original.Revision);
        EditResult updated = await store.UpdateRowAsync(
                database.Path,
                catalog,
                update,
                CancellationToken.None)
            .ConfigureAwait(false);
        history.Record(update, [RowReplayGuard.Present("data", updated.CurrentRow!)]);
        Assert.IsTrue(history.State.IsDirty);

        EditHistoryReplay undoUpdate = history.TakeUndoReplay();
        EditReplayResult updateUndone = await replayer.ReplayAsync(
                database.Path,
                catalog,
                undoUpdate,
                CancellationToken.None)
            .ConfigureAwait(false);
        history.CompleteUndo(undoUpdate, updateUndone.OppositeGuards);
        Assert.AreEqual("old", await database.ScalarAsync<string>("SELECT value FROM data WHERE ID=1").ConfigureAwait(false));
        Assert.IsFalse(history.State.IsDirty);

        EditHistoryReplay redoUpdate = history.TakeRedoReplay();
        EditReplayResult updateRedone = await replayer.ReplayAsync(
                database.Path,
                catalog,
                redoUpdate,
                CancellationToken.None)
            .ConfigureAwait(false);
        history.CompleteRedo(redoUpdate, updateRedone.OppositeGuards);
        history.MarkSavedBaseline();
        Assert.IsFalse(history.State.IsDirty);

        TypedRow beforeDelete = (await store.QueryAsync(
                database.Path,
                catalog,
                new TableQuery("data", new PageRequest(0, 10)),
                CancellationToken.None)
            .ConfigureAwait(false)).Rows.Single();
        var deletion = new RowDeletionOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, beforeDelete);
        EditResult deleted = await store.DeleteRowAsync(
                database.Path,
                catalog,
                deletion,
                CancellationToken.None)
            .ConfigureAwait(false);
        history.Record(deletion, [RowReplayGuard.Absent("data", deleted.CurrentRow!.Identity!)]);
        EditHistoryReplay undoDelete = history.TakeUndoReplay();
        EditReplayResult deleteUndone = await replayer.ReplayAsync(
                database.Path,
                catalog,
                undoDelete,
                CancellationToken.None)
            .ConfigureAwait(false);
        history.CompleteUndo(undoDelete, deleteUndone.OppositeGuards);
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM data").ConfigureAwait(false));
        Assert.AreEqual("000102", await database.ScalarAsync<string>("SELECT hex(payload) FROM data").ConfigureAwait(false));

        var insertion = new RowInsertionOperation(
            Guid.NewGuid(),
            "data",
            DateTimeOffset.UtcNow,
            new Dictionary<string, SqliteValue>
            {
                ["ID"] = SqliteValue.Integer(2),
                ["value"] = SqliteValue.Text("inserted"),
                ["optional"] = SqliteValue.Null,
            });
        EditResult inserted = await store.InsertRowAsync(
                database.Path,
                catalog,
                insertion,
                CancellationToken.None)
            .ConfigureAwait(false);
        var recordedInsertion = new RowInsertionOperation(
            insertion.OperationId,
            insertion.TableName,
            insertion.CreatedAtUtc,
            insertion.Values,
            inserted.CurrentRow!.Identity,
            inserted.CurrentRow);
        history.Record(recordedInsertion, [RowReplayGuard.Present("data", inserted.CurrentRow)]);
        EditHistoryReplay undoInsertion = history.TakeUndoReplay();
        EditReplayResult insertionUndone = await replayer.ReplayAsync(
                database.Path,
                catalog,
                undoInsertion,
                CancellationToken.None)
            .ConfigureAwait(false);
        history.CompleteUndo(undoInsertion, insertionUndone.OppositeGuards);
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM data WHERE ID=2").ConfigureAwait(false));

        var reloaded = new EditHistory(Path.Combine(database.Directory, "edit-history.json"));
        Assert.IsTrue(reloaded.State.CanRedo);
        Assert.IsFalse(reloaded.State.IsDirty);
    }
}
