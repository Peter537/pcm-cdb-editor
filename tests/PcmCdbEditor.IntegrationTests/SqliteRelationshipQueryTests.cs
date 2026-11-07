using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class SqliteRelationshipQueryTests
{
    [TestMethod]
    [DataRow(ForeignKeyDisplayMode.RawValue)]
    [DataRow(ForeignKeyDisplayMode.ResolvedName)]
    [DataRow(ForeignKeyDisplayMode.RawAndName)]
    public async Task SearchAndExplicitTargetsAgreeAcrossPagesCountsAndAllRows(ForeignKeyDisplayMode mode)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_team(IDteam INTEGER, gene_sz_name TEXT)",
            "INSERT INTO STA_team VALUES(2,'Zulu'), (10,'Alpha'), (20,'Duplicate Alpha'), (20,'Other'), (30,NULL)",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDteam INTEGER, note TEXT)",
            "INSERT INTO data VALUES(1,2,'first'), (2,10,'second'), (3,10,'third'), (4,20,'duplicate'), (5,999,'missing'), (6,30,'blank'), (7,NULL,'null')")
            .ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var query = new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest("Alpha", ["fkIDteam"]), foreignKeyDisplayMode: mode);
        await AssertQueryIdsAsync(database, catalog, query, [2, 3]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", query.Page,
            search: new GlobalSearchRequest("10", ["fkIDteam"]), foreignKeyDisplayMode: mode), [2, 3]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", query.Page,
            filter: new FilterCondition("fkIDteam", FilterOperator.Equals, SqliteValue.Text("alpha"), QueryValueTarget.RelatedLabel),
            foreignKeyDisplayMode: mode), [2, 3]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", query.Page,
            filter: new FilterCondition("fkIDteam", FilterOperator.Equals, SqliteValue.Integer(2)),
            foreignKeyDisplayMode: mode), [1]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", query.Page,
            filter: new FilterCondition("fkIDteam", FilterOperator.Equals, SqliteValue.Text("2"), QueryValueTarget.RelatedLabel),
            foreignKeyDisplayMode: mode), []).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", query.Page,
            filter: new FilterCondition("fkIDteam", FilterOperator.IsNull, SqliteValue.Null, QueryValueTarget.RelatedLabel),
            foreignKeyDisplayMode: mode), [4, 5, 6, 7]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", query.Page,
            filter: new FilterCondition("fkIDteam", FilterOperator.IsNotNull, SqliteValue.Null, QueryValueTarget.RelatedLabel),
            sorts: [new SortDescriptor("fkIDteam", SortDirection.Ascending, QueryValueTarget.RelatedLabel)],
            foreignKeyDisplayMode: mode), [2, 3, 1]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", query.Page,
            filter: new FilterCondition("fkIDteam", FilterOperator.IsNotNull, SqliteValue.Null, QueryValueTarget.RelatedLabel),
            sorts: [new SortDescriptor("fkIDteam", SortDirection.Ascending)],
            foreignKeyDisplayMode: mode), [1, 2, 3]).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task PhysicalDisplaySuffixAndRevisionSurviveEveryDisplayModeAndLabelChanges()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_team(IDteam INTEGER PRIMARY KEY, gene_sz_name TEXT)",
            "INSERT INTO STA_team VALUES(1,'First')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDteam INTEGER, fkIDteam__display TEXT, note TEXT)",
            "INSERT INTO data VALUES(1,1,'physical-value','original')").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        TypedRow? first = null;
        foreach (ForeignKeyDisplayMode mode in Enum.GetValues<ForeignKeyDisplayMode>())
        {
            var row = (await store.QueryRowsAsync(database.Path, catalog,
                new TableQuery("data", new PageRequest(0, 1), foreignKeyDisplayMode: mode),
                CancellationToken.None).ConfigureAwait(false)).Rows.Single();
            first ??= row;
            Assert.AreEqual(first.Revision, row.Revision);
            Assert.HasCount(4, row.Values);
            Assert.AreEqual("physical-value", row.Values["fkIDteam__display"].TextValue);
            if (mode != ForeignKeyDisplayMode.RawValue)
            {
                Assert.AreEqual("First", row.RelatedValues["fkIDteam"].Label);
            }
        }

        await database.ExecuteAsync("UPDATE STA_team SET gene_sz_name='Renamed'").ConfigureAwait(false);
        var update = new CellUpdateOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, first!.Identity!,
            "note", first.Values["note"], SqliteValue.Text("changed"), first.Revision);
        var result = await store.UpdateCellAsync(database.Path, catalog, update, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("changed", result.CurrentRow!.Values["note"].TextValue);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            filter: new FilterCondition("fkIDteam__display", FilterOperator.Equals, SqliteValue.Text("physical-value")),
            sorts: [new SortDescriptor("fkIDteam__display", SortDirection.Ascending)]), [1]).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RiderLabelsAndLiteralSearchUseTheSameCompleteName()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_cyclist(IDcyclist INTEGER PRIMARY KEY, gene_sz_firstname TEXT, gene_sz_lastname TEXT)",
            "INSERT INTO DYN_cyclist VALUES(1,'  Ada  ','  Rider%_\\  '), (2,NULL,' Solo '), (3,' ','')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDcyclist INTEGER)",
            "INSERT INTO data VALUES(1,1), (2,2), (3,3)").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var rows = (await store.QueryRowsAsync(database.Path, catalog,
            new TableQuery("data", new PageRequest(0, 10)), CancellationToken.None).ConfigureAwait(false)).Rows;
        Assert.AreEqual("Ada Rider%_\\", rows[0].RelatedValues["fkIDcyclist"].Label);
        Assert.AreEqual("Solo", rows[1].RelatedValues["fkIDcyclist"].Label);
        Assert.AreEqual(RelationshipResolution.LabelUnavailable, rows[2].RelatedValues["fkIDcyclist"].Resolution);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest("Ada Rider%_\\", ["fkIDcyclist"])), [1]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            filter: new FilterCondition("fkIDcyclist", FilterOperator.Contains, SqliteValue.Text("%_\\"), QueryValueTarget.RelatedLabel)),
            [1]).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ParentAffinityAndCollationDefineCardinalityForProjectionAndQueries()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_key(IDkey TEXT COLLATE NOCASE, gene_sz_name TEXT)",
            "INSERT INTO STA_key VALUES('A','First'), ('a','Duplicate'), ('01','Leading zero'), ('1','Plain one'), ('B','Bee')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDkey)",
            "INSERT INTO data VALUES(1,'a'), (2,1), (3,'01'), (4,'b'), (5,1.0)").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var rows = (await new SqliteTableDataStore().QueryRowsAsync(database.Path, catalog,
            new TableQuery("data", new PageRequest(0, 10)), CancellationToken.None).ConfigureAwait(false)).Rows;
        Assert.AreEqual(RelationshipResolution.AmbiguousKey, rows[0].RelatedValues["fkIDkey"].Resolution);
        Assert.AreEqual("Plain one", rows[1].RelatedValues["fkIDkey"].Label);
        Assert.AreEqual("Leading zero", rows[2].RelatedValues["fkIDkey"].Label);
        Assert.AreEqual("Bee", rows[3].RelatedValues["fkIDkey"].Label);
        Assert.AreEqual(RelationshipResolution.MissingTarget, rows[4].RelatedValues["fkIDkey"].Resolution);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest("Plain one", ["fkIDkey"])), [2]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            filter: new FilterCondition("fkIDkey", FilterOperator.Equals, SqliteValue.Text("Bee"), QueryValueTarget.RelatedLabel)),
            [4]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest("First", ["fkIDkey"])), []).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RelatedKeyUsesParentTextAffinityEvenWhenSourceColumnIsNumeric()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_key(IDkey TEXT, gene_sz_name TEXT)",
            "INSERT INTO STA_key VALUES('01','Leading zero')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDkey INTEGER)",
            "INSERT INTO data VALUES(1,1)").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var row = (await new SqliteTableDataStore().QueryRowsAsync(database.Path, catalog,
            new TableQuery("data", new PageRequest(0, 1)), CancellationToken.None).ConfigureAwait(false)).Rows.Single();
        Assert.AreEqual(RelationshipResolution.MissingTarget, row.RelatedValues["fkIDkey"].Resolution);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest("Leading zero", ["fkIDkey"])), []).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InvalidQueryTargetsAndNonTextLabelComparisonsAreRejected()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_key(IDkey INTEGER, gene_sz_name TEXT)",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDkey INTEGER)").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        foreach (FilterOperator op in new[] { FilterOperator.Equals, FilterOperator.NotEquals })
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.QueryRowsAsync(database.Path, catalog,
                new TableQuery("data", new PageRequest(0, 1),
                    filter: new FilterCondition("fkIDkey", op, SqliteValue.Integer(1), QueryValueTarget.RelatedLabel)),
                CancellationToken.None)).ConfigureAwait(false);
        }

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.QueryRowsAsync(database.Path, catalog,
            new TableQuery("data", new PageRequest(0, 1),
                filter: new FilterCondition("fkIDkey", FilterOperator.Equals, SqliteValue.Integer(1), (QueryValueTarget)42)),
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.QueryRowsAsync(database.Path, catalog,
            new TableQuery("data", new PageRequest(0, 1),
                sorts: [new SortDescriptor("fkIDkey", SortDirection.Ascending, (QueryValueTarget)42)]),
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.QueryRowsAsync(database.Path, catalog,
            new TableQuery("data", new PageRequest(0, 1),
                sorts: [new SortDescriptor("fkIDkey__display", SortDirection.Ascending)]),
            CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RelationshipsWithoutLabelColumnsStillReportMissingAndDuplicateKeys()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_bare(IDbare INTEGER)",
            "INSERT INTO STA_bare VALUES(1),(3),(3)",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDbare INTEGER)",
            "INSERT INTO data VALUES(1,1),(2,2),(3,3)").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var rows = await store.QueryAllRowsAsync(database.Path, catalog,
            new TableQueryDefinition("data"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(RelationshipResolution.LabelUnavailable, rows[0].RelatedValues["fkIDbare"].Resolution);
        Assert.AreEqual(RelationshipResolution.MissingTarget, rows[1].RelatedValues["fkIDbare"].Resolution);
        Assert.AreEqual(RelationshipResolution.AmbiguousKey, rows[2].RelatedValues["fkIDbare"].Resolution);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.CountAsync(database.Path, catalog,
            new TableQuery("data", new PageRequest(0, 10), filter:
                new FilterCondition("fkIDbare", FilterOperator.IsNull, SqliteValue.Null, QueryValueTarget.RelatedLabel)),
            CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task UnicodeWhitespaceIsNormalizedForLabelsFiltersAndSearch()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_cyclist(IDcyclist INTEGER PRIMARY KEY, gene_sz_firstname TEXT, gene_sz_lastname TEXT)",
            "INSERT INTO DYN_cyclist VALUES(1,char(9)||'Ada'||char(160),char(160)||'Rider'||char(9)), (2,char(9),char(160))",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDcyclist INTEGER)",
            "INSERT INTO data VALUES(1,1),(2,2)").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var rows = await new SqliteTableDataStore().QueryAllRowsAsync(database.Path, catalog,
            new TableQueryDefinition("data"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("Ada Rider", rows[0].RelatedValues["fkIDcyclist"].Label);
        Assert.AreEqual(RelationshipResolution.LabelUnavailable, rows[1].RelatedValues["fkIDcyclist"].Resolution);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest("Ada Rider", ["fkIDcyclist"])), [1]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1), filter:
            new FilterCondition("fkIDcyclist", FilterOperator.IsNull, SqliteValue.Null, QueryValueTarget.RelatedLabel)),
            [2]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest(" \t\u00a0", ["fkIDcyclist"])), [1, 2]).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RawSearchIncludesUntypedTextAndNumbersAndExcludesActualBlobs()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, value BLOB)",
            "INSERT INTO data VALUES(1,'Needle'),(2,421),(3,X'4E6565646C65')").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest("Needle", ["value"])), [1]).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest("421", ["value"])), [2]).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RelationshipQueriesQualifyPhysicalTablesThatCollideWithInternalNames()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE __pcm_keys(id INTEGER PRIMARY KEY,name TEXT); INSERT INTO __pcm_keys VALUES(1,'First')",
            "CREATE TABLE __pcm_resolved(id INTEGER PRIMARY KEY,name TEXT); INSERT INTO __pcm_resolved VALUES(1,'Second')",
            "CREATE TABLE __pcm_relation_0(id INTEGER PRIMARY KEY,name TEXT); INSERT INTO __pcm_relation_0 VALUES(1,'Third')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, first INTEGER REFERENCES __pcm_keys(id), second INTEGER REFERENCES __pcm_resolved(id), third INTEGER REFERENCES __pcm_relation_0(id))",
            "INSERT INTO data VALUES(1,1,1,1)").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var row = (await new SqliteTableDataStore().QueryRowsAsync(database.Path, catalog,
            new TableQuery("data", new PageRequest(0, 1)), CancellationToken.None).ConfigureAwait(false)).Rows.Single();
        Assert.AreEqual("First", row.RelatedValues["first"].Label);
        Assert.AreEqual("Second", row.RelatedValues["second"].Label);
        Assert.AreEqual("Third", row.RelatedValues["third"].Label);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest("Third", ["first", "second", "third"])), [1]).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SearchSupportsMoreThanSqlitesOuterJoinLimit()
    {
        string[] columns = Enumerable.Range(0, 70).Select(index => $"ref{index}").ToArray();
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE target(id INTEGER PRIMARY KEY,name TEXT); INSERT INTO target VALUES(1,'Needle')",
            $"CREATE TABLE data(ID INTEGER PRIMARY KEY, {string.Join(", ", columns.Select(name => $"{name} INTEGER REFERENCES target(id)"))})",
            $"INSERT INTO data VALUES(1,{string.Join(", ", columns.Select((_, index) => index == 69 ? "1" : "NULL"))})")
            .ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        await AssertQueryIdsAsync(database, catalog, new TableQuery("data", new PageRequest(0, 1),
            search: new GlobalSearchRequest("Needle", columns), foreignKeyDisplayMode: ForeignKeyDisplayMode.RawValue),
            [1]).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task UnsupportedRelationshipsKeepExplicitFallbacksAndRejectLabelQueryTargets()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE parent (a INTEGER, b INTEGER, name TEXT, PRIMARY KEY(a,b))",
            "INSERT INTO parent VALUES(1,2,'tuple')",
            "CREATE TABLE STA_bare(IDbare INTEGER PRIMARY KEY)",
            "INSERT INTO STA_bare VALUES(1)",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, a INTEGER, b INTEGER, fkIDunknown INTEGER, fkIDbare INTEGER, FOREIGN KEY(a,b) REFERENCES parent(a,b))",
            "INSERT INTO data VALUES(1,1,2,8,1)").ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var row = (await store.QueryRowsAsync(database.Path, catalog, new TableQuery("data", new PageRequest(0, 1)),
            CancellationToken.None).ConfigureAwait(false)).Rows.Single();
        Assert.AreEqual(RelationshipResolution.CompositeRelationship, row.RelatedValues["a"].Resolution);
        Assert.AreEqual(RelationshipResolution.CompositeRelationship, row.RelatedValues["b"].Resolution);
        Assert.AreEqual(RelationshipResolution.UnknownRelationship, row.RelatedValues["fkIDunknown"].Resolution);
        Assert.AreEqual(RelationshipResolution.LabelUnavailable, row.RelatedValues["fkIDbare"].Resolution);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.QueryRowsAsync(database.Path, catalog,
            new TableQuery("data", new PageRequest(0, 1), sorts: [new SortDescriptor("a", SortDirection.Ascending, QueryValueTarget.RelatedLabel)]),
            CancellationToken.None)).ConfigureAwait(false);

        TableSchema source = catalog.Tables.Single(table => table.Name == "data");
        var ambiguous = new TableSchema(source.Name, source.ObjectKind, source.Columns,
            source.Relationships.Concat(
            [
                new ForeignKeyRelation("fkIDbare", "parent", "a", "name", false, "test"),
                new ForeignKeyRelation("fkIDunknown", "missing_table", "ID", "name", true, "test")
            ]),
            source.StableIdentity, source.EditCapability, source.EstimatedRowCount, source.IsWithoutRowId);
        var changedCatalog = new DatabaseSchemaCatalog(catalog.SchemaSignature,
            catalog.Tables.Select(table => table.Name == source.Name ? ambiguous : table));
        var changedRow = (await store.QueryRowsAsync(database.Path, changedCatalog,
            new TableQuery("data", new PageRequest(0, 1)), CancellationToken.None).ConfigureAwait(false)).Rows.Single();
        Assert.AreEqual(RelationshipResolution.AmbiguousRelationship, changedRow.RelatedValues["fkIDbare"].Resolution);
        Assert.AreEqual(RelationshipResolution.TargetUnavailable, changedRow.RelatedValues["fkIDunknown"].Resolution);
    }

    [TestMethod]
    public async Task StagedNumericKeysUseTextParentAffinityJustLikeFinitePages()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup TEXT, gene_sz_name TEXT)",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDlookup INTEGER)",
            """
            WITH RECURSIVE numbers(value) AS (VALUES(1) UNION ALL SELECT value+1 FROM numbers WHERE value<401)
            INSERT INTO STA_lookup SELECT CAST(value AS TEXT), 'Target ' || value FROM numbers;
            INSERT INTO data SELECT CAST(IDlookup AS INTEGER), CAST(IDlookup AS INTEGER) FROM STA_lookup;
            """).ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        var definition = new TableQueryDefinition("data");

        var finite = await store.QueryRowsAsync(database.Path, catalog,
            new TableQuery(definition, new PageRequest(400, 1)), CancellationToken.None).ConfigureAwait(false);
        var all = await store.QueryAllRowsAsync(database.Path, catalog, definition, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.HasCount(401, all);
        Assert.IsTrue(all.All(row => row.RelatedValues["fkIDlookup"].Resolution == RelationshipResolution.Resolved));
        Assert.AreEqual("Target 401", all[^1].RelatedValues["fkIDlookup"].Label);
        Assert.AreEqual(finite.Rows.Single().RelatedValues["fkIDlookup"], all[^1].RelatedValues["fkIDlookup"]);
    }

    private static async Task AssertQueryIdsAsync(
        SqliteTestDatabase database,
        DatabaseSchemaCatalog catalog,
        TableQuery query,
        long[] expectedIds)
    {
        var store = new SqliteTableDataStore();
        Assert.AreEqual((long)expectedIds.Length,
            await store.CountAsync(database.Path, catalog, query, CancellationToken.None).ConfigureAwait(false));
        var all = await store.QueryAllRowsAsync(database.Path, catalog, query.Definition, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(expectedIds, all.Select(row => row.Values["ID"].IntegerValue).ToArray());
        var pagedIds = new List<long>();
        for (int offset = 0; offset <= expectedIds.Length; offset++)
        {
            var pageQuery = new TableQuery(query.TableName, new PageRequest(offset, 1), query.Sorts,
                query.Filter, query.Search, query.ForeignKeyDisplayMode);
            var page = await store.QueryRowsAsync(database.Path, catalog, pageQuery, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(offset + 1 < expectedIds.Length, page.HasMore);
            pagedIds.AddRange(page.Rows.Select(row => row.Values["ID"].IntegerValue));
        }

        CollectionAssert.AreEqual(expectedIds, pagedIds);
    }
}
