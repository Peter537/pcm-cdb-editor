using System.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class SqliteCatalogAndStoreTests
{
    private static readonly string[] CompositeIdentityColumns = ["IDteam", "IDrider"];
    private static readonly string?[] ResolvedCountryOrder = ["999", "Belgium", "Denmark"];

    [TestMethod]
    public async Task CatalogDiscoversCompositeRowIdViewAndConservativeRelationships()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_country(IDcountry INTEGER PRIMARY KEY, gene_sz_name TEXT)",
            "CREATE TABLE DYN_rider(IDteam INTEGER NOT NULL, IDrider INTEGER NOT NULL, fkIDcountry INTEGER, PRIMARY KEY(IDteam, IDrider), FOREIGN KEY(fkIDcountry) REFERENCES STA_country(IDcountry)) WITHOUT ROWID",
            "INSERT INTO STA_country VALUES(1,'Neutral'); INSERT INTO DYN_rider VALUES(1,1,1)",
            "CREATE TABLE notes(value TEXT)",
            "CREATE VIEW rider_names AS SELECT IDrider FROM DYN_rider",
            "CREATE VIRTUAL TABLE rider_search USING fts5(value)").ConfigureAwait(false);

        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(catalog.TryGetTable("DYN_rider", out var rider));
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
        var store = new SqliteTableDataStore();
        var viewRows = await store.QueryAsync(
            database.Path,
            catalog,
            new TableQuery("rider_names", new PageRequest(0, 10)),
            CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, viewRows.Rows);
        Assert.IsNull(viewRows.Rows[0].Identity);
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
        var raw = await store.QueryAsync(
            database.Path,
            catalog,
            new TableQuery("DYN_rider", new PageRequest(0, 1), search: search,
                foreignKeyDisplayMode: ForeignKeyDisplayMode.RawValue),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, raw.Rows.Count);
        Assert.AreEqual(1L, raw.TotalRows);
        Assert.IsFalse(raw.Rows[0].Values.ContainsKey("fkIDcountry__display"));
        Assert.AreEqual(SqliteValueKind.Blob, raw.Rows[0].Values["payload"].Kind);
        Assert.AreEqual(SqliteValueKind.Null, raw.Rows[0].Values["optional"].Kind);
        await database.ExecuteAsync("UPDATE DYN_rider SET gene_sz_name='back\\slash' WHERE IDrider=3")
            .ConfigureAwait(false);
        var slashSearch = await store.QueryAsync(
            database.Path,
            catalog,
            new TableQuery("DYN_rider", new PageRequest(0, 10),
                search: new GlobalSearchRequest("\\", ["gene_sz_name"])),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, slashSearch.TotalRows);

        var resolved = await store.QueryAsync(
            database.Path,
            catalog,
            new TableQuery("DYN_rider", new PageRequest(0, 3),
                [new SortDescriptor("fkIDcountry__display", SortDirection.Ascending)],
                foreignKeyDisplayMode: ForeignKeyDisplayMode.ResolvedName),
            CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            ResolvedCountryOrder,
            resolved.Rows.Select(row => row.Values["fkIDcountry__display"].TextValue).ToArray());
        var rawAndName = await store.QueryAsync(
            database.Path,
            catalog,
            new TableQuery("DYN_rider", new PageRequest(0, 3),
                foreignKeyDisplayMode: ForeignKeyDisplayMode.RawAndName),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("1 | Denmark", rawAndName.Rows[0].Values["fkIDcountry__display"].TextValue);
        Assert.AreEqual("999", rawAndName.Rows[1].Values["fkIDcountry__display"].TextValue);
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
    public async Task CrudUsesGeneratedIdentityAndRejectsStaleRevisionWithoutPartialUpdate()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_item(IDitem INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, quantity INTEGER NOT NULL)",
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
}
