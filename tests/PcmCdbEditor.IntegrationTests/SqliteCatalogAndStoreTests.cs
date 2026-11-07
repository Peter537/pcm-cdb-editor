using System.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.History;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class SqliteCatalogAndStoreTests
{
    private static readonly string[] CompositeIdentityColumns = ["IDteam", "IDrider"];
    private static readonly string[] NullableKeyNotes = ["first", "second", "third"];
    private static readonly string?[] ResolvedCountryOrder = [null, "Belgium", "Denmark"];

    [TestMethod]
    public async Task CatalogDiscoversCompositeRowIdViewAndConservativeRelationships()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_country(IDcountry INTEGER PRIMARY KEY, gene_sz_name TEXT)",
            "CREATE TABLE DYN_rider(IDteam INTEGER NOT NULL, IDrider INTEGER NOT NULL, fkIDcountry INTEGER, PRIMARY KEY(IDteam, IDrider), FOREIGN KEY(fkIDcountry) REFERENCES STA_country(IDcountry)) WITHOUT /* comment */ ROWID",
            "INSERT INTO STA_country VALUES(1,'Neutral'); INSERT INTO DYN_rider VALUES(1,1,1)",
            "CREATE TABLE notes(value TEXT)",
            "CREATE VIEW rider_names AS SELECT IDrider FROM DYN_rider",
            "CREATE VIRTUAL TABLE rider_search USING fts5(value)").ConfigureAwait(false);

        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(catalog.TryGetTable("DYN_rider", out var rider));
        Assert.IsTrue(rider.IsWithoutRowId);
        Assert.AreEqual(StableIdentityKind.DeclaredPrimaryKey, rider.StableIdentity.Kind);
        CollectionAssert.AreEqual(CompositeIdentityColumns, rider.StableIdentity.Columns.ToArray());
        Assert.HasCount(1, rider.Relationships);
        Assert.AreEqual("gene_sz_name", rider.Relationships[0].DisplayColumn);
        Assert.IsTrue(catalog.TryGetTable("notes", out var notes));
        Assert.AreEqual(StableIdentityKind.RowIdFallback, notes.StableIdentity.Kind);
        Assert.IsTrue(catalog.TryGetTable("rider_names", out var view));
        Assert.AreEqual(TableEditCapability.ReadOnlyView, view.EditCapability);
        Assert.AreEqual(StableIdentityKind.None, view.StableIdentity.Kind);
        Assert.IsTrue(catalog.TryGetTable("rider_search", out var virtualTable));
        Assert.AreEqual(TableEditCapability.UnsupportedSchema, virtualTable.EditCapability);
        Assert.AreEqual(StableIdentityKind.None, virtualTable.StableIdentity.Kind);
        Assert.IsTrue(catalog.TryGetTable("rider_search_content", out var shadowTable));
        Assert.AreEqual(TableEditCapability.UnsupportedSchema, shadowTable.EditCapability);
        Assert.AreEqual(StableIdentityKind.None, shadowTable.StableIdentity.Kind);
        var store = new SqliteTableDataStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InsertRowAsync(
            database.Path,
            catalog,
            new RowInsertionOperation(Guid.NewGuid(), shadowTable.Name, DateTimeOffset.UtcNow,
                [KeyValuePair.Create("c0", SqliteValue.Text("do not bypass FTS"))]),
            CancellationToken.None)).ConfigureAwait(false);
        var viewRows = await store.QueryAsync(
            database.Path,
            catalog,
            new TableQuery("rider_names", new PageRequest(0, 10)),
            CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, viewRows.Rows);
        Assert.IsNull(viewRows.Rows[0].Identity);
    }

    [TestMethod]
    public async Task LegalNullPrimaryKeysRemainBrowsableWithStablePaging()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE data(id TEXT PRIMARY KEY, rowid TEXT, note TEXT DEFAULT 'WITHOUT ROWID')",
            "INSERT INTO data VALUES(NULL, 'shadow', 'first'), (NULL, 'shadow', 'second'), ('key', 'shadow', 'third')")
            .ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var definition = new TableQueryDefinition("data", [new SortDescriptor("id", SortDirection.Ascending)]);

        var all = await store.QueryAllRowsAsync(database.Path, catalog, definition, CancellationToken.None)
            .ConfigureAwait(false);
        var secondPage = await store.QueryAsync(database.Path, catalog,
                new TableQuery(definition, new PageRequest(1, 1)), CancellationToken.None)
            .ConfigureAwait(false);

        CollectionAssert.AreEqual(NullableKeyNotes,
            all.Select(row => row.Values["note"].TextValue).ToArray());
        Assert.IsNull(all[0].Identity);
        Assert.IsNull(all[1].Identity);
        Assert.IsNotNull(all[2].Identity);
        Assert.AreEqual("second", secondPage.Rows.Single().Values["note"].TextValue);
    }

    [TestMethod]
    public async Task CatalogRejectsDeclaredRowIdColumnAsUnsafeFallbackIdentity()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE shadowed_identity(rowid INTEGER, value TEXT)",
            "INSERT INTO shadowed_identity(rowid, value) VALUES(NULL, 'first'), (NULL, 'second'), (7, 'third'), (7, 'fourth')")
            .ConfigureAwait(false);

        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(catalog.TryGetTable("shadowed_identity", out var shadowed));
        Assert.AreEqual(StableIdentityKind.None, shadowed.StableIdentity.Kind);
        Assert.AreEqual(TableEditCapability.MissingStableIdentity, shadowed.EditCapability);

        var page = await new SqliteTableDataStore().QueryAsync(
            database.Path,
            catalog,
            new TableQuery("shadowed_identity", new PageRequest(0, 10)),
            CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(4, page.Rows);
        Assert.IsTrue(page.Rows.All(static row => row.Identity is null));
    }

    [TestMethod]
    public async Task QueryIsBoundedParameterizedTypedAndProjectsAllForeignKeyModes()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_country(IDcountry INTEGER PRIMARY KEY, gene_sz_name TEXT)",
            "INSERT INTO STA_country VALUES(1, 'Denmark'), (2, 'Belgium')",
            "CREATE TABLE DYN_rider(IDrider INTEGER PRIMARY KEY, fkIDcountry INTEGER, gene_sz_name TEXT, score REAL, payload BLOB, optional TEXT)",
            "INSERT INTO DYN_rider VALUES(1, 1, '100%_safe', 9.5, X'000102', NULL), (2, 999, 'quoted'' rider', 3, X'FF', 'x'), (3, 2, 'other', 7, NULL, NULL)")
            .ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var filter = new FilterCondition(
            "gene_sz_name",
            FilterOperator.Equals,
            SqliteValue.Text("quoted' rider' OR 1=1 --"));
        Assert.AreEqual(0, await store.CountAsync(
            database.Path, catalog, "DYN_rider", filter, CancellationToken.None).ConfigureAwait(false));

        var search = new GlobalSearchRequest("100%_", ["gene_sz_name", "payload"]);
        var rawQuery = new TableQuery(
            "DYN_rider",
            new PageRequest(0, 1),
            search: search,
            foreignKeyDisplayMode: ForeignKeyDisplayMode.RawValue);
        var raw = await store.QueryAsync(
            database.Path,
            catalog,
            rawQuery,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, raw.Rows.Count);
        Assert.AreEqual(1L, raw.TotalRows);
        Assert.IsFalse(raw.Rows[0].Values.ContainsKey("fkIDcountry__display"));
        Assert.AreEqual(SqliteValueKind.Blob, raw.Rows[0].Values["payload"].Kind);
        Assert.AreEqual(SqliteValueKind.Null, raw.Rows[0].Values["optional"].Kind);
        IReadOnlyList<TypedRow> allRaw = await store.QueryAllRowsAsync(
            database.Path,
            catalog,
            rawQuery.Definition,
            CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, allRaw);
        Assert.IsFalse(allRaw[0].Values.ContainsKey("fkIDcountry__display"));
        await database.ExecuteAsync("UPDATE DYN_rider SET gene_sz_name='back\\slash' WHERE IDrider=3")
            .ConfigureAwait(false);
        var slashSearch = await store.QueryAsync(
            database.Path,
            catalog,
            new TableQuery("DYN_rider", new PageRequest(0, 10),
                search: new GlobalSearchRequest("\\", ["gene_sz_name"])),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, slashSearch.TotalRows);

        var resolvedQuery = new TableQuery(
            "DYN_rider",
            new PageRequest(0, 3),
            [new SortDescriptor("fkIDcountry", SortDirection.Ascending, QueryValueTarget.RelatedLabel)],
            foreignKeyDisplayMode: ForeignKeyDisplayMode.ResolvedName);
        var resolved = await store.QueryAsync(
            database.Path,
            catalog,
            resolvedQuery,
            CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            ResolvedCountryOrder,
            resolved.Rows.Select(row => row.RelatedValues["fkIDcountry"].Label).ToArray());
        IReadOnlyList<TypedRow> allResolved = await store.QueryAllRowsAsync(
            database.Path,
            catalog,
            resolvedQuery.Definition,
            CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            ResolvedCountryOrder,
            allResolved.Select(row => row.RelatedValues["fkIDcountry"].Label).ToArray());
        var rawAndNameQuery = new TableQuery(
            "DYN_rider",
            new PageRequest(0, 3),
            foreignKeyDisplayMode: ForeignKeyDisplayMode.RawAndName);
        var rawAndName = await store.QueryAsync(
            database.Path,
            catalog,
            rawAndNameQuery,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("Denmark", rawAndName.Rows[0].RelatedValues["fkIDcountry"].Label);
        Assert.AreEqual(RelationshipResolution.MissingTarget, rawAndName.Rows[1].RelatedValues["fkIDcountry"].Resolution);
        IReadOnlyList<TypedRow> allRawAndName = await store.QueryAllRowsAsync(
            database.Path,
            catalog,
            rawAndNameQuery.Definition,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("Denmark", allRawAndName[0].RelatedValues["fkIDcountry"].Label);
        Assert.AreEqual(RelationshipResolution.MissingTarget, allRawAndName[1].RelatedValues["fkIDcountry"].Resolution);
    }

    [TestMethod]
    public async Task CountFreeSlicesStayBoundedAndCountTheSameDatabaseSideQueryLazily()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_item(IDitem INTEGER PRIMARY KEY, bucket INTEGER, label TEXT)")
            .ConfigureAwait(false);
        await database.ExecuteAsync(@"
WITH RECURSIVE sequence(value) AS (
  VALUES(1)
  UNION ALL
  SELECT value + 1 FROM sequence WHERE value < 1201
)
INSERT INTO DYN_item(IDitem, bucket, label)
SELECT value, 0, 'row-' || value FROM sequence;")
            .ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var middleQuery = new TableQuery(
            "DYN_item",
            new PageRequest(500, 500),
            [new SortDescriptor("bucket", SortDirection.Ascending)],
            new FilterCondition("IDitem", FilterOperator.GreaterThanOrEqual, SqliteValue.Integer(1)),
            new GlobalSearchRequest("row-", ["label"]),
            ForeignKeyDisplayMode.RawValue);

        TableSlice middle = await store.QueryRowsAsync(
            database.Path,
            catalog,
            middleQuery,
            CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(500, middle.Rows);
        Assert.IsTrue(middle.HasMore);
        Assert.AreEqual(501L, middle.Rows[0].Values["IDitem"].IntegerValue);
        Assert.AreEqual(1_201L, await store.CountAsync(
            database.Path,
            catalog,
            middleQuery,
            CancellationToken.None).ConfigureAwait(false));

        var finalQuery = new TableQuery(
            "DYN_item",
            new PageRequest(1_000, 500),
            middleQuery.Sorts,
            middleQuery.Filter,
            middleQuery.Search,
            middleQuery.ForeignKeyDisplayMode);
        TableSlice final = await store.QueryRowsAsync(
            database.Path,
            catalog,
            finalQuery,
            CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(201, final.Rows);
        Assert.IsFalse(final.HasMore);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.QueryRowsAsync(
            database.Path,
            catalog,
            middleQuery,
            cancelled.Token)).ConfigureAwait(false);
        TableSlice recovered = await store.QueryRowsAsync(
            database.Path,
            catalog,
            new TableQuery("DYN_item", new PageRequest(0, 10)),
            CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(10, recovered.Rows);
    }

    [TestMethod]
    public async Task AllRowsUseOnePageIndependentQueryBeyondTheBoundedPageLimit()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_item(IDitem INTEGER PRIMARY KEY, label TEXT)")
            .ConfigureAwait(false);
        await database.ExecuteAsync(@"
WITH RECURSIVE sequence(value) AS (
  VALUES(1)
  UNION ALL
  SELECT value + 1 FROM sequence WHERE value < 10025
)
INSERT INTO DYN_item(IDitem, label)
SELECT value, 'row-' || value FROM sequence;")
            .ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var definition = new TableQueryDefinition(
            "DYN_item",
            [new SortDescriptor("IDitem", SortDirection.Descending)],
            new FilterCondition("IDitem", FilterOperator.GreaterThanOrEqual, SqliteValue.Integer(1)),
            new GlobalSearchRequest("row-", ["label"]),
            ForeignKeyDisplayMode.RawValue);
        var commands = new List<SqliteTableDataStore.CommandKind>();
        var store = new SqliteTableDataStore(commands.Add);
        string commandText = SqliteTableDataStore.BuildAllRowsCommandTextForDiagnostics(
            catalog,
            definition);

        IReadOnlyList<TypedRow> rows = await store.QueryAllRowsAsync(
            database.Path,
            catalog,
            definition,
            CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(10_025, rows);
        Assert.AreEqual(10_025L, rows[0].Values["IDitem"].IntegerValue);
        Assert.AreEqual(1L, rows[^1].Values["IDitem"].IntegerValue);
        Assert.IsFalse(commandText.Contains(" LIMIT ", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(commandText.Contains("$offset", StringComparison.Ordinal));
        CollectionAssert.AreEqual(
            new[] { SqliteTableDataStore.CommandKind.Page },
            commands.ToArray());

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.QueryAllRowsAsync(
            database.Path,
            catalog,
            definition,
            cancelled.Token)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(499)]
    [DataRow(500)]
    [DataRow(501)]
    [DataRow(1_201)]
    public async Task AllRowsReturnExactCountsAcrossBoundariesForReadOnlyViews(int rowCount)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_item(IDitem INTEGER PRIMARY KEY, label TEXT)",
            "CREATE VIEW item_view AS SELECT IDitem, label FROM DYN_item")
            .ConfigureAwait(false);
        if (rowCount > 0)
        {
            await database.ExecuteAsync($@"
WITH RECURSIVE sequence(value) AS (
  VALUES(1)
  UNION ALL
  SELECT value + 1 FROM sequence WHERE value < {rowCount}
)
INSERT INTO DYN_item(IDitem, label)
SELECT value, 'row-' || value FROM sequence;")
                .ConfigureAwait(false);
        }

        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var definition = new TableQueryDefinition(
            "item_view",
            [new SortDescriptor("IDitem", SortDirection.Ascending)],
            new FilterCondition("IDitem", FilterOperator.GreaterThanOrEqual, SqliteValue.Integer(1)),
            search: null,
            foreignKeyDisplayMode: ForeignKeyDisplayMode.RawValue);

        IReadOnlyList<TypedRow> rows = await new SqliteTableDataStore().QueryAllRowsAsync(
            database.Path,
            catalog,
            definition,
            CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(rowCount, rows);
        Assert.IsTrue(rows.All(static row => row.Identity is null));
        if (rowCount > 0)
        {
            Assert.AreEqual(1L, rows[0].Values["IDitem"].IntegerValue);
            Assert.AreEqual((long)rowCount, rows[^1].Values["IDitem"].IntegerValue);
        }
    }

    [TestMethod]
    public async Task UpdateReturnsStoredAffinityAndRecognizesAnEffectiveNoOp()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_item(IDitem INTEGER PRIMARY KEY, quantity INTEGER NOT NULL)",
            "INSERT INTO DYN_item VALUES(1, 5)").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        TypedRow before = (await store.QueryAsync(
            database.Path, catalog, new TableQuery("DYN_item", new PageRequest(0, 10)), CancellationToken.None)
            .ConfigureAwait(false)).Rows.Single();
        var update = new CellUpdateOperation(Guid.NewGuid(), "DYN_item", DateTimeOffset.UtcNow,
            before.Identity!, "quantity", SqliteValue.Integer(5), SqliteValue.Text("0007"), before.Revision);

        EditResult changed = await store.UpdateCellAsync(database.Path, catalog, update, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(1, changed.AffectedRows);
        TypedRow after = changed.CurrentRow!;
        Assert.AreEqual(SqliteValue.Integer(7), after.Values["quantity"]);

        EditResult unchanged = await store.UpdateCellAsync(database.Path, catalog,
            update with { OldValue = after.Values["quantity"], ExpectedRevision = after.Revision }, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(0, unchanged.AffectedRows);
        Assert.AreEqual(after.Revision, unchanged.CurrentRow?.Revision);
    }

    [TestMethod]
    public async Task CrudUsesGeneratedIdentityAndRejectsStaleRevisionWithoutPartialUpdate()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_item(IDitem INTEGER PRIMARY KEY, name TEXT NOT NULL, quantity INTEGER NOT NULL)",
            "INSERT INTO DYN_item(name, quantity) VALUES('old', 1)").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var page = await store.QueryAsync(
            database.Path, catalog, new TableQuery("DYN_item", new PageRequest(0, 10)), CancellationToken.None)
            .ConfigureAwait(false);
        Assert.HasCount(1, page.Rows);
        var original = page.Rows[0];

        var update = new CellUpdateOperation(
            Guid.NewGuid(), "DYN_item", DateTimeOffset.UtcNow, original.Identity!, "name",
            original.Values["name"], SqliteValue.Text("new"), original.Revision);
        var updated = await store.UpdateCellAsync(database.Path, catalog, update, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual("new", updated.CurrentRow!.Values["name"].TextValue);

        var stale = new RowUpdateOperation(
            Guid.NewGuid(), "DYN_item", DateTimeOffset.UtcNow, original.Identity!,
            original.Values,
            new Dictionary<string, SqliteValue>
            {
                ["name"] = SqliteValue.Text("partial"),
                ["quantity"] = SqliteValue.Integer(99)
            },
            original.Revision);
        await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() =>
            store.UpdateRowAsync(database.Path, catalog, stale, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1L, await database.ScalarAsync<long>(
            "SELECT quantity FROM DYN_item WHERE IDitem=1").ConfigureAwait(false));

        var insertion = new RowInsertionOperation(
            Guid.NewGuid(), "DYN_item", DateTimeOffset.UtcNow,
            new Dictionary<string, SqliteValue>
            {
                ["name"] = SqliteValue.Text("inserted"),
                ["quantity"] = SqliteValue.Integer(2)
            });
        var inserted = await store.InsertRowAsync(database.Path, catalog, insertion, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(inserted.CurrentRow);
        Assert.AreEqual(2L, inserted.CurrentRow.Identity!.Components[0].Value.IntegerValue);
        var deletion = new RowDeletionOperation(
            Guid.NewGuid(), "DYN_item", DateTimeOffset.UtcNow, inserted.CurrentRow);
        Assert.AreEqual(1, (await store.DeleteRowAsync(
            database.Path, catalog, deletion, CancellationToken.None).ConfigureAwait(false)).AffectedRows);
    }

    [TestMethod]
    [DataRow("CREATE TABLE data (id INT PRIMARY KEY DEFAULT 7, note TEXT)")]
    [DataRow("CREATE TABLE data (id INTEGER PRIMARY KEY DESC DEFAULT 7, note TEXT)")]
    [DataRow("CREATE TABLE data (id INT DEFAULT 7, scope TEXT DEFAULT 'x', note TEXT, PRIMARY KEY(scope, id)) WITHOUT ROWID")]
    [DataRow("CREATE TABLE data (id INTEGER DEFAULT 7, note TEXT)")]
    public async Task InsertUndoRedoUsesReturnedIdentityAndPreservesOriginalRow(string createTable)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            createTable, "INSERT INTO data (id, note) VALUES (2, 'original')").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var insertion = new RowInsertionOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow,
            [KeyValuePair.Create("note", SqliteValue.Text("inserted"))]);
        var inserted = await new SqliteTableDataStore().InsertRowAsync(
            database.Path, catalog, insertion, CancellationToken.None).ConfigureAwait(false);
        TypedRow row = inserted.CurrentRow!;
        Assert.AreEqual(SqliteValue.Integer(7), row.Values["id"]);
        Assert.AreEqual(SqliteValue.Text("inserted"), row.Values["note"]);
        var recorded = new RowInsertionOperation(insertion.OperationId, "data", insertion.CreatedAtUtc,
            insertion.Values, insertedRow: row);
        var replayer = new SqliteEditOperationReplayer();
        var undo = await replayer.ReplayAsync(database.Path, catalog,
            new EditHistoryReplay(recorded, EditReplayDirection.Undo, [RowReplayGuard.Present("data", row)]),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM data").ConfigureAwait(false));
        Assert.AreEqual("original", await database.ScalarAsync<string>(
            "SELECT note FROM data WHERE id = 2").ConfigureAwait(false));

        await replayer.ReplayAsync(database.Path, catalog,
            new EditHistoryReplay(recorded, EditReplayDirection.Redo, undo.OppositeGuards), CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(2L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM data").ConfigureAwait(false));
        Assert.AreEqual("original", await database.ScalarAsync<string>(
            "SELECT note FROM data WHERE id = 2").ConfigureAwait(false));
        Assert.AreEqual("inserted", await database.ScalarAsync<string>(
            "SELECT note FROM data WHERE id = 7").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InsertRollsBackWhenReturnedKeyIsNullOrDiffersFromAssignedIdentity()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE data (id INTEGER PRIMARY KEY DESC, note TEXT)",
            "INSERT INTO data VALUES (2, 'original')").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var noKey = new RowInsertionOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, []);
        var nullError = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(
            () => store.InsertRowAsync(database.Path, catalog, noKey, CancellationToken.None)).ConfigureAwait(false);
        StringAssert.Contains(nullError.Message, "NULL", StringComparison.Ordinal);
        Assert.AreEqual("id", nullError.ColumnName);
        var wrongKey = new RowInsertionOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow,
            [KeyValuePair.Create("id", SqliteValue.Integer(7))],
            RowIdentity.FromPrimaryKey([new RowIdentityComponent("id", SqliteValue.Integer(2))]));
        var mismatch = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(
            () => store.InsertRowAsync(database.Path, catalog, wrongKey, CancellationToken.None)).ConfigureAwait(false);
        StringAssert.Contains(mismatch.Message, "different row identity", StringComparison.Ordinal);
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM data").ConfigureAwait(false));
        Assert.AreEqual("original", await database.ScalarAsync<string>(
            "SELECT note FROM data WHERE id = 2").ConfigureAwait(false));
    }
}
