using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.History;
using PcmCdbEditor.Infrastructure.Internal;
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
            history.MarkSavedBaseline();
            var pending = history.TakeUndo();
            history.CompleteUndo(pending);

            var reloaded = new EditHistory(path);
            EditHistorySnapshot snapshot = reloaded.GetSnapshot();
            Assert.AreEqual(1, snapshot.State.UndoCount);
            Assert.AreEqual(1, snapshot.State.RedoCount);
            Assert.AreEqual(2, snapshot.SavedAppliedCount);
            Assert.AreEqual(delete.OperationId, snapshot.NextUndo?.OperationId);
            Assert.AreEqual(insert.OperationId, snapshot.NextRedo?.OperationId);
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
    public async Task SavingAfterCommittedInterruptedUndoRetiresTheAmbiguousTimeline()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, value TEXT)",
            "INSERT INTO data VALUES(1,'before')").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        TypedRow before = (await store.QueryAsync(database.Path, catalog,
            new TableQuery("data", new PageRequest(0, 10)), CancellationToken.None).ConfigureAwait(false)).Rows.Single();
        var update = new CellUpdateOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, before.Identity!, "value",
            SqliteValue.Text("before"), SqliteValue.Text("after"), before.Revision);
        EditResult edited = await store.UpdateCellAsync(database.Path, catalog, update, CancellationToken.None).ConfigureAwait(false);
        string historyPath = Path.Combine(database.Directory, "interrupted-history.json");
        var history = new EditHistory(historyPath);
        history.Record(update, [RowReplayGuard.Present("data", edited.CurrentRow!)]);
        var replayer = new SqliteEditOperationReplayer();
        await replayer.ReplayAsync(database.Path, catalog, history.TakeUndoReplay(), CancellationToken.None).ConfigureAwait(false);

        var recovered = new EditHistory(historyPath, recovering: true);
        Assert.IsTrue(recovered.GetSnapshot().State.HistoryUncertain);
        Assert.AreEqual("before", await database.ScalarAsync<string>("SELECT value FROM data WHERE ID=1").ConfigureAwait(false));
        recovered.MarkSavedBaseline();

        EditHistorySnapshot saved = new EditHistory(historyPath).GetSnapshot();
        Assert.IsFalse(saved.State.IsDirty);
        Assert.IsFalse(saved.State.HistoryUncertain);
        Assert.AreEqual(0, saved.SavedAppliedCount);
        Assert.IsEmpty(saved.Applied);
        Assert.IsEmpty(saved.Undone);
        Assert.IsNull(saved.NextUndo);
    }

    [TestMethod]
    public async Task RetiringStrandedReplayRequiresDurableBaselineBeforeClearingPendingState()
    {
        await using var database = await SqliteTestDatabase.CreateAsync("CREATE TABLE data(ID INTEGER PRIMARY KEY)")
            .ConfigureAwait(false);
        string historyPath = Path.Combine(database.Directory, "stranded-history.json");
        var history = new EditHistory(historyPath);
        var operation = new RowInsertionOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow,
            [KeyValuePair.Create("ID", SqliteValue.Integer(1))], RowIdentity.FromRowId(1));
        history.Record(operation);
        _ = history.TakeUndoReplay();
        using (File.Open(historyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                history.MarkSavedBaseline(retireUncertainHistory: true);
                Assert.Fail("A locked history destination cannot accept the new baseline.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Assert.IsTrue(history.State.HasPendingReplay);
                Assert.IsTrue(history.State.IsDirty);
            }
        }

        history.MarkSavedBaseline(retireUncertainHistory: true);
        Assert.IsFalse(history.State.HasPendingReplay);
        Assert.IsFalse(history.State.IsDirty);
        Assert.IsFalse(new EditHistory(historyPath).State.CanUndo);
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
    public async Task ConcurrentPreferenceAndTableViewStateSavesPreserveBothUpdates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var preferences = new EditorPreferences(
                ApplicationTheme.Dark,
                GridDensity.Comfortable,
                pageSize: 250,
                ForeignKeyDisplayMode.ResolvedName,
                ["C:\\databases\\recent.cdb"]);
            var viewState = new TableViewState(
                "signature",
                "DYN_table",
                [new ColumnDisplayState("value", 147.5, 0, true, false)],
                GridDensity.Comfortable,
                frozenColumnCount: 1);
            var serializationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueSerialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task initialWrite = Task.Run(() => AtomicJsonFile.WriteAsync(
                path,
                new GateBlockingSettingsDocument(serializationStarted, continueSerialization),
                CancellationToken.None));

            await serializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Task preferenceSave = new JsonSettingsStore(path)
                .SavePreferencesAsync(preferences, CancellationToken.None);
            Task viewStateSave = new JsonSettingsStore(path)
                .SaveTableViewStateAsync(viewState, CancellationToken.None);
            continueSerialization.SetResult();

            await Task.WhenAll(initialWrite, preferenceSave, viewStateSave).ConfigureAwait(false);

            var reloaded = new JsonSettingsStore(path);
            EditorPreferences actualPreferences = await reloaded
                .LoadPreferencesAsync(CancellationToken.None)
                .ConfigureAwait(false);
            TableViewState? actualViewState = await reloaded
                .LoadTableViewStateAsync("SIGNATURE", "dyn_TABLE", CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(preferences.Theme, actualPreferences.Theme);
            Assert.AreEqual(preferences.Density, actualPreferences.Density);
            Assert.AreEqual(preferences.PageSize, actualPreferences.PageSize);
            Assert.AreEqual(preferences.ForeignKeyDisplayMode, actualPreferences.ForeignKeyDisplayMode);
            CollectionAssert.AreEqual(preferences.RecentFiles.ToArray(), actualPreferences.RecentFiles.ToArray());
            Assert.IsNotNull(actualViewState);
            Assert.AreEqual(viewState.SchemaSignature, actualViewState.SchemaSignature);
            Assert.AreEqual(viewState.TableName, actualViewState.TableName);
            Assert.AreEqual(viewState.Density, actualViewState.Density);
            Assert.AreEqual(viewState.FrozenColumnCount, actualViewState.FrozenColumnCount);
            CollectionAssert.AreEqual(viewState.Columns.ToArray(), actualViewState.Columns.ToArray());
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
    public async Task LegacyPersistedSortsAreIgnoredAndRemovedWhenColumnLayoutIsResaved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                    path,
                    """
                    {
                      "PREFERENCES": {
                        "THEME": 0,
                        "DENSITY": 0,
                        "PAGESIZE": 100,
                        "FOREIGNKEYDISPLAYMODE": 2,
                        "RECENTFILES": []
                      },
                      "TABLEVIEWSTATES": {
                        "signature\u001fDYN_table": {
                          "SCHEMASIGNATURE": "signature",
                          "TABLENAME": "DYN_table",
                          "COLUMNS": [
                            {
                              "COLUMNNAME": "value",
                              "WIDTH": 147.5,
                              "DISPLAYINDEX": 2,
                              "ISVISIBLE": false,
                              "ISFROZEN": true
                            }
                          ],
                          "SORTS": [
                            {
                              "COLUMNNAME": "value",
                              "DIRECTION": 1
                            }
                          ],
                          "DENSITY": 1,
                          "FROZENCOLUMNCOUNT": 1
                        }
                      }
                    }
                    """)
                .ConfigureAwait(false);

            var store = new JsonSettingsStore(path);
            TableViewState? restored = await store.LoadTableViewStateAsync(
                    "SIGNATURE",
                    "dyn_TABLE",
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsNotNull(restored);
            Assert.IsNull(
                typeof(TableViewState).GetProperty("Sorts"),
                "Legacy sort descriptors must not re-enter the persisted view-state model.");
            Assert.AreEqual("DYN_table", restored.TableName);
            Assert.AreEqual(GridDensity.Comfortable, restored.Density);
            Assert.AreEqual(1, restored.FrozenColumnCount);
            Assert.HasCount(1, restored.Columns);
            ColumnDisplayState column = restored.Columns[0];
            Assert.AreEqual("value", column.ColumnName);
            Assert.AreEqual(147.5, column.Width);
            Assert.AreEqual(2, column.DisplayIndex);
            Assert.IsFalse(column.IsVisible);
            Assert.IsTrue(column.IsFrozen);

            await store.SaveTableViewStateAsync(restored, CancellationToken.None).ConfigureAwait(false);

            string rewrittenJson = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            Assert.IsFalse(
                rewrittenJson.Contains("\"Sorts\"", StringComparison.OrdinalIgnoreCase),
                "Rewriting a legacy layout must retire its obsolete persisted sort descriptors.");
            TableViewState? reloaded = await store.LoadTableViewStateAsync(
                    "signature",
                    "DYN_table",
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(reloaded);
            Assert.AreEqual(restored.Columns[0], reloaded.Columns[0]);
            Assert.AreEqual(restored.Density, reloaded.Density);
            Assert.AreEqual(restored.FrozenColumnCount, reloaded.FrozenColumnCount);
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
    [DataRow(-1, 100)]
    [DataRow(0, 100)]
    [DataRow(42, 100)]
    [DataRow(100, 100)]
    [DataRow(250, 250)]
    [DataRow(500, 500)]
    [DataRow(501, 100)]
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
    public async Task FiveHundredRowPreferenceRoundTripsWithoutAnAllRowsSentinel()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonSettingsStore(path);
            await store.SavePreferencesAsync(
                    new EditorPreferences(
                        ApplicationTheme.Dark,
                        GridDensity.Comfortable,
                        pageSize: 500,
                        ForeignKeyDisplayMode.RawAndName),
                    CancellationToken.None)
                .ConfigureAwait(false);

            EditorPreferences reloaded = await store
                .LoadPreferencesAsync(CancellationToken.None)
                .ConfigureAwait(false);
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);

            Assert.AreEqual(500, reloaded.PageSize);
            StringAssert.Contains(json, "\"PageSize\": 500", StringComparison.Ordinal);
            Assert.IsFalse(json.Contains("All rows", StringComparison.OrdinalIgnoreCase));
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

    private sealed class GateBlockingSettingsDocument(
        TaskCompletionSource serializationStarted,
        TaskCompletionSource continueSerialization)
    {
        [System.Text.Json.Serialization.JsonPropertyOrder(-1)]
        public string SerializationMarker
        {
            get
            {
                serializationStarted.TrySetResult();
                continueSerialization.Task.GetAwaiter().GetResult();
                return "ready";
            }
        }

        public Dictionary<string, object> Preferences { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Theme"] = ApplicationTheme.System,
            ["Density"] = GridDensity.Compact,
            ["PageSize"] = 100,
            ["ForeignKeyDisplayMode"] = ForeignKeyDisplayMode.RawAndName,
            ["RecentFiles"] = Array.Empty<string>()
        };

        public Dictionary<string, object> TableViewStates { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
