using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.History;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class SqliteRelationshipLookupTests
{
    private readonly SqliteRelationshipLookup _lookup = new();
    private readonly SqliteTableDataStore _store = new();

    [TestMethod]
    public async Task SearchPreservesUnusualTypedKeysAndDisambiguatesDuplicateNames()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup, gene_sz_name TEXT, code TEXT)",
            "INSERT INTO STA_lookup VALUES(-3,'Negative','n'),(0,'Duplicate','zero'),(2.5,'Duplicate','fraction'),('', 'Empty key','e'),('001','Text key','t'),('percent','100%_\\ literal','literal'),(7,'One duplicate key','a'),(7,'Other duplicate key','b'),(NULL,'Null key','n'),(X'1234','Blob key','b')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDlookup)").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        RelationshipLookupPage all = await SearchAsync(database, catalog).ConfigureAwait(false);
        Assert.HasCount(6, all.Options);
        Assert.IsFalse(all.HasMore);
        CollectionAssert.AreEquivalent(new[] { SqliteValue.Integer(-3), SqliteValue.Integer(0), SqliteValue.Real(2.5),
            SqliteValue.Text(""), SqliteValue.Text("001"), SqliteValue.Text("percent") }, all.Options.Select(option => option.Key).ToArray());
        RelationshipLookupOption[] duplicates = all.Options.Where(option => option.Label == "Duplicate").ToArray();
        Assert.HasCount(2, duplicates);
        Assert.AreNotEqual(duplicates[0].Context, duplicates[1].Context);
        Assert.AreNotEqual(duplicates[0].TargetIdentity, duplicates[1].TargetIdentity);
        Assert.HasCount(1, (await SearchAsync(database, catalog, "%_\\").ConfigureAwait(false)).Options);
        Assert.HasCount(1, (await SearchAsync(database, catalog, "fraction").ConfigureAwait(false)).Options);
        Assert.HasCount(0, (await SearchAsync(database, catalog, "One duplicate key").ConfigureAwait(false)).Options);
        Assert.HasCount(1, (await SearchAsync(database, catalog, "001").ConfigureAwait(false)).Options);
    }

    [TestMethod]
    public async Task SearchBoundsResultsAndUsesSharedWhitespaceLabelPolicy()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_cyclist(IDcyclist INTEGER PRIMARY KEY, gene_sz_firstname TEXT, gene_sz_lastname TEXT)",
            "INSERT INTO DYN_cyclist VALUES(0,char(9)||'Ada'||char(160),char(160)||'Lovelace'||char(9))",
            "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<250) INSERT INTO DYN_cyclist SELECT x,'Rider',CAST(x AS TEXT) FROM n",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDcyclist INTEGER)").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        var page = await _lookup.SearchAsync(database.Path, catalog, "data", "fkIDcyclist", null, 5, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(5, page.Options);
        Assert.IsTrue(page.HasMore);
        var ada = await _lookup.SearchAsync(database.Path, catalog, "data", "fkIDcyclist", "Ada Lovelace", 10, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("Ada Lovelace", Assert.ContainsSingle(ada.Options).Label);
        Assert.IsFalse(ada.HasMore);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            _lookup.SearchAsync(database.Path, catalog, "data", "fkIDcyclist", null, 201, CancellationToken.None)).ConfigureAwait(false);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _lookup.SearchAsync(database.Path, catalog, "data", "fkIDcyclist", null, 10, canceled.Token)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ResolveUsesParentAffinityAndCollationAndReportsAmbiguousOrUnsupportedKeys()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup TEXT COLLATE NOCASE, gene_sz_name TEXT)",
            "INSERT INTO STA_lookup VALUES('AbC','Unique'),('dup','One'),('DUP','Two'),('1','Number')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDlookup)").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        RelationshipLookupResult unique = await ResolveAsync(database, catalog, SqliteValue.Text("abc")).ConfigureAwait(false);
        Assert.AreEqual(RelationshipResolution.Resolved, unique.Resolution);
        Assert.AreEqual(SqliteValue.Text("AbC"), unique.Option!.Key);
        Assert.AreEqual(SqliteValue.Text("1"), (await ResolveAsync(database, catalog, SqliteValue.Integer(1)).ConfigureAwait(false)).Option!.Key);
        Assert.AreEqual(RelationshipResolution.AmbiguousKey, (await ResolveAsync(database, catalog, SqliteValue.Text("dUp")).ConfigureAwait(false)).Resolution);
        Assert.AreEqual(RelationshipResolution.MissingTarget, (await ResolveAsync(database, catalog, SqliteValue.Text("missing")).ConfigureAwait(false)).Resolution);
        Assert.AreEqual(RelationshipResolution.MissingTarget, (await ResolveAsync(database, catalog, SqliteValue.Null).ConfigureAwait(false)).Resolution);
        var blob = await ResolveAsync(database, catalog, SqliteValue.Blob([1, 2])).ConfigureAwait(false);
        Assert.IsNull(blob.Option);
        StringAssert.Contains(blob.UnavailableReason!, "BLOB", StringComparison.Ordinal);
        CollectionAssert.AreEquivalent((string[]) ["AbC", "1"], (await SearchAsync(database, catalog).ConfigureAwait(false)).Options.Select(option => option.Key.TextValue).ToArray());
    }

    [TestMethod]
    public async Task LabelLessTargetsAndInternalNameCollisionsStillResolvePrecisely()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE __pcm_counts(__pcm_value0 TEXT PRIMARY KEY, __pcm_label TEXT, __pcm_count INTEGER) WITHOUT ROWID",
            "INSERT INTO __pcm_counts VALUES('key','context',1)",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDlookup TEXT REFERENCES __pcm_counts(__pcm_value0))").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        var option = Assert.ContainsSingle((await SearchAsync(database, catalog).ConfigureAwait(false)).Options);
        Assert.AreEqual(SqliteValue.Text("key"), option.Key);
        Assert.IsNull(option.Label);
        Assert.AreEqual(RowIdentityKind.DeclaredPrimaryKey, option.TargetIdentity.Kind);
        var resolved = await ResolveAsync(database, catalog, option.Key).ConfigureAwait(false);
        Assert.AreEqual(RelationshipResolution.LabelUnavailable, resolved.Resolution);
        Assert.AreEqual(option, resolved.Option);
    }

    [TestMethod]
    public async Task UnsupportedMetadataAndNullableTargetIdentityDoNotOfferMisleadingChoices()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER, nullable_key TEXT PRIMARY KEY, gene_sz_name TEXT)",
            "INSERT INTO STA_lookup VALUES(1,NULL,'No identity'),(2,'valid','Valid')",
            "CREATE TABLE pair(a INTEGER,b INTEGER,PRIMARY KEY(a,b))",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY,fkIDlookup INTEGER,fkIDunknown INTEGER,a INTEGER,b INTEGER,FOREIGN KEY(a,b) REFERENCES pair(a,b))").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        Assert.AreEqual(SqliteValue.Integer(2), Assert.ContainsSingle((await SearchAsync(database, catalog).ConfigureAwait(false)).Options).Key);
        Assert.IsNull((await ResolveAsync(database, catalog, SqliteValue.Integer(1)).ConfigureAwait(false)).Option);
        var composite = await _lookup.ResolveAsync(database.Path, catalog, "data", "a", SqliteValue.Integer(1), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(RelationshipResolution.CompositeRelationship, composite.Resolution);
        Assert.IsNull(composite.Option);
        Assert.AreEqual(RelationshipResolution.UnknownRelationship,
            (await _lookup.ResolveAsync(database.Path, catalog, "data", "fkIDunknown", SqliteValue.Integer(1), CancellationToken.None).ConfigureAwait(false)).Resolution);
    }

    [TestMethod]
    [DataRow("-3", SqliteValueKind.Integer)]
    [DataRow("0", SqliteValueKind.Integer)]
    [DataRow("2.5", SqliteValueKind.Real)]
    [DataRow("''", SqliteValueKind.Text)]
    [DataRow("'001'", SqliteValueKind.Text)]
    public async Task ChosenTypedKeysUpdateOneFieldAndUndoRedoRemainRaw(string keySql, SqliteValueKind kind)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup, gene_sz_name TEXT)",
            $"INSERT INTO STA_lookup VALUES({keySql},'Chosen')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDlookup, note TEXT)",
            "INSERT INTO data VALUES(1,'unresolved','untouched')").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        TypedRow before = await RowAsync(database, catalog).ConfigureAwait(false);
        RelationshipLookupOption option = Assert.ContainsSingle((await SearchAsync(database, catalog).ConfigureAwait(false)).Options);
        Assert.AreEqual(kind, option.Key.Kind);
        RowUpdateOperation operation = Update(before, option);
        EditResult result = await _store.UpdateRowAsync(database.Path, catalog, operation, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(option.Key, result.CurrentRow!.Values["fkIDlookup"]);
        Assert.AreEqual(before.Values["note"], result.CurrentRow.Values["note"]);
        // An inferred target may disappear after the edit. History restores the exact
        // physical keys, without turning that inference into a new write constraint.
        await database.ExecuteAsync("DELETE FROM STA_lookup").ConfigureAwait(false);
        var replayer = new SqliteEditOperationReplayer();
        var undo = await replayer.ReplayAsync(database.Path, catalog,
            new EditHistoryReplay(operation, EditReplayDirection.Undo, [RowReplayGuard.Present("data", result.CurrentRow)]), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(before.Values["fkIDlookup"], (await RowAsync(database, catalog).ConfigureAwait(false)).Values["fkIDlookup"]);
        await replayer.ReplayAsync(database.Path, catalog,
            new EditHistoryReplay(operation, EditReplayDirection.Redo, undo.OppositeGuards), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(option.Key, (await RowAsync(database, catalog).ConfigureAwait(false)).Values["fkIDlookup"]);
    }

    [TestMethod]
    [DataRow("UPDATE STA_lookup SET gene_sz_name='Changed'")]
    [DataRow("DELETE FROM STA_lookup")]
    [DataRow("INSERT INTO STA_lookup VALUES(2,'Chosen','other')")]
    [DataRow("DELETE FROM STA_lookup; INSERT INTO STA_lookup(rowid,IDlookup,gene_sz_name,note) VALUES(50,2,'Chosen','original')")]
    [DataRow("UPDATE data SET note='external'")]
    public async Task StaleSelectionsAndSourceRowsAreRecoverableRefusals(string change)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER, gene_sz_name TEXT, note TEXT)",
            "INSERT INTO STA_lookup VALUES(2,'Chosen','original')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDlookup INTEGER,note TEXT)",
            "INSERT INTO data VALUES(1,1,'original')").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        TypedRow before = await RowAsync(database, catalog).ConfigureAwait(false);
        var option = Assert.ContainsSingle((await SearchAsync(database, catalog).ConfigureAwait(false)).Options);
        await database.ExecuteAsync(change).ConfigureAwait(false);
        TypedRow actualBefore = await RowAsync(database, catalog).ConfigureAwait(false);
        var exception = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
            _store.UpdateRowAsync(database.Path, catalog, Update(before, option), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("fkIDlookup", exception.ColumnName);
        Assert.AreEqual(actualBefore.Revision, (await RowAsync(database, catalog).ConfigureAwait(false)).Revision);
    }

    [TestMethod]
    public async Task SourceAffinityCannotSilentlyRedirectChoiceAndRollsBackOtherAssignments()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup TEXT PRIMARY KEY,gene_sz_name TEXT)",
            "INSERT INTO STA_lookup VALUES('001','Chosen'),('1','Wrong target')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY,fkIDlookup INTEGER,note TEXT)",
            "INSERT INTO data VALUES(1,99,'original')").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        TypedRow before = await RowAsync(database, catalog).ConfigureAwait(false);
        var option = Assert.ContainsSingle((await SearchAsync(database, catalog, "Chosen").ConfigureAwait(false)).Options);
        var operation = new RowUpdateOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, before.Identity!,
            before.Values, new Dictionary<string, SqliteValue> { ["fkIDlookup"] = option.Key, ["note"] = SqliteValue.Text("tentative") },
            before.Revision, new Dictionary<string, RelationshipLookupOption> { ["fkIDlookup"] = option });
        var exception = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
            _store.UpdateRowAsync(database.Path, catalog, operation, CancellationToken.None)).ConfigureAwait(false);
        StringAssert.Contains(exception.Message, "converted", StringComparison.Ordinal);
        Assert.AreEqual("fkIDlookup", exception.ColumnName);
        Assert.AreEqual(before.Revision, (await RowAsync(database, catalog).ConfigureAwait(false)).Revision);
    }

    [TestMethod]
    public async Task CompatibleAffinityReturnsActualStoredTypeAndManualRawOrNullRemainAvailable()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER PRIMARY KEY,gene_sz_name TEXT)",
            "INSERT INTO STA_lookup VALUES(2,'Chosen')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY,fkIDlookup TEXT,note TEXT)",
            "INSERT INTO data VALUES(1,'original','unchanged')").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        TypedRow before = await RowAsync(database, catalog).ConfigureAwait(false);
        var option = Assert.ContainsSingle((await SearchAsync(database, catalog).ConfigureAwait(false)).Options);
        var result = await _store.UpdateRowAsync(database.Path, catalog, Update(before, option), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SqliteValue.Text("2"), result.CurrentRow!.Values["fkIDlookup"]);
        foreach (SqliteValue raw in new[] { SqliteValue.Text("unresolved"), SqliteValue.Null })
        {
            var row = result.CurrentRow!;
            var operation = new RowUpdateOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, row.Identity!,
                [KeyValuePair.Create("fkIDlookup", row.Values["fkIDlookup"])], [KeyValuePair.Create("fkIDlookup", raw)], row.Revision);
            result = await _store.UpdateRowAsync(database.Path, catalog, operation, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(raw, result.CurrentRow!.Values["fkIDlookup"]);
        }
    }

    [TestMethod]
    public async Task CancelledSearchReturnsNoResultAndCompletedSearchRemainsBounded()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER,gene_sz_name TEXT,context TEXT)",
            "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<150000) INSERT INTO STA_lookup SELECT x,'Name '||x,'Context '||x FROM n",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY,fkIDlookup INTEGER)",
            "INSERT INTO data VALUES(1,0)").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _lookup.SearchAsync(database.Path, catalog, "data", "fkIDlookup", "Context", 10,
                new CancellationToken(canceled: true))).ConfigureAwait(false);
        RelationshipLookupPage page = await _lookup.SearchAsync(
            database.Path, catalog, "data", "fkIDlookup", "Name", 10, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(10, page.Options);
        Assert.IsTrue(page.HasMore);
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT fkIDlookup FROM data").ConfigureAwait(false));
        Assert.AreEqual(150000L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM STA_lookup").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task MismatchedChoiceAndCancellationLeaveEveryFieldUnchanged()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER PRIMARY KEY,gene_sz_name TEXT)",
            "INSERT INTO STA_lookup VALUES(1,'One'),(2,'Two')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY,fkIDlookup INTEGER,note TEXT)",
            "INSERT INTO data VALUES(1,0,'original')").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        TypedRow row = await RowAsync(database, catalog).ConfigureAwait(false);
        RelationshipLookupOption choice = (await SearchAsync(database, catalog, "One").ConfigureAwait(false)).Options.Single();
        var operation = new RowUpdateOperation(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, row.Identity!, row.Values,
            new Dictionary<string, SqliteValue> { ["fkIDlookup"] = SqliteValue.Integer(2), ["note"] = SqliteValue.Text("tentative") },
            row.Revision, [KeyValuePair.Create("fkIDlookup", choice)]);
        var error = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
            _store.UpdateRowAsync(database.Path, catalog, operation, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("fkIDlookup", error.ColumnName);
        Assert.AreEqual(row.Revision, (await RowAsync(database, catalog).ConfigureAwait(false)).Revision);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _store.UpdateRowAsync(database.Path, catalog, Update(row, choice), cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(row.Revision, (await RowAsync(database, catalog).ConfigureAwait(false)).Revision);
    }

    [TestMethod]
    public async Task CompositeTargetIdentityIsPreciseForSingleColumnRelationship()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(a TEXT,b INTEGER,IDlookup TEXT UNIQUE,gene_sz_name TEXT,PRIMARY KEY(a,b)) WITHOUT ROWID",
            "INSERT INTO STA_lookup VALUES('a',1,'key','Chosen')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY,fkIDlookup TEXT REFERENCES STA_lookup(IDlookup))",
            "INSERT INTO data VALUES(1,NULL)").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        RelationshipLookupOption choice = (await SearchAsync(database, catalog).ConfigureAwait(false)).Options.Single();
        Assert.HasCount(2, choice.TargetIdentity.Components);
        Assert.AreEqual(SqliteValue.Text("a"), choice.TargetIdentity.Components[0].Value);
        Assert.AreEqual(SqliteValue.Integer(1), choice.TargetIdentity.Components[1].Value);
        TypedRow row = await RowAsync(database, catalog).ConfigureAwait(false);
        var result = await _store.UpdateRowAsync(database.Path, catalog, Update(row, choice), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(choice.Key, result.CurrentRow!.Values["fkIDlookup"]);
    }

    [TestMethod]
    public async Task SelfReferenceMayChangeItsOwnRowRevisionAfterChoiceValidation()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER PRIMARY KEY,gene_sz_name TEXT,fkIDlookup INTEGER REFERENCES STA_lookup(IDlookup))",
            "INSERT INTO STA_lookup VALUES(1,'Self',NULL)").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        RelationshipLookupOption choice = (await _lookup.SearchAsync(database.Path, catalog, "STA_lookup", "fkIDlookup", null, 5, CancellationToken.None).ConfigureAwait(false)).Options.Single();
        TypedRow row = (await _store.QueryRowsAsync(database.Path, catalog, new TableQuery("STA_lookup", new PageRequest(0, 5)), CancellationToken.None).ConfigureAwait(false)).Rows.Single();
        var operation = new RowUpdateOperation(Guid.NewGuid(), "STA_lookup", DateTimeOffset.UtcNow, row.Identity!,
            [KeyValuePair.Create("fkIDlookup", SqliteValue.Null)], [KeyValuePair.Create("fkIDlookup", choice.Key)], row.Revision,
            [KeyValuePair.Create("fkIDlookup", choice)]);
        var result = await _store.UpdateRowAsync(database.Path, catalog, operation, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(choice.Key, result.CurrentRow!.Values["fkIDlookup"]);
        Assert.AreNotEqual(row.Revision, result.CurrentRow.Revision);
    }

    [TestMethod]
    public async Task SameTypedChoiceValidatesWithoutIssuingUpdateOrRequiringTriggerSafety()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER PRIMARY KEY,gene_sz_name TEXT)",
            "INSERT INTO STA_lookup VALUES(2,'Chosen')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY,fkIDlookup INTEGER,note TEXT)",
            "INSERT INTO data VALUES(1,2,'original')",
            "CREATE TABLE audit(value INTEGER)",
            "CREATE TRIGGER record_update AFTER UPDATE ON data BEGIN INSERT INTO audit VALUES(1); END").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        TypedRow row = await RowAsync(database, catalog).ConfigureAwait(false);
        RelationshipLookupOption choice = (await SearchAsync(database, catalog).ConfigureAwait(false)).Options.Single();
        var result = await _store.UpdateRowAsync(database.Path, catalog, Update(row, choice), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, result.AffectedRows);
        Assert.AreEqual(row.Revision, result.CurrentRow!.Revision);
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM audit").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("UPDATE STA_lookup SET gene_sz_name='changed'")]
    [DataRow("DELETE FROM STA_lookup")]
    [DataRow("UPDATE data SET note='changed'")]
    public async Task SameTypedChoiceStillRejectsStaleTargetOrSource(string change)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER PRIMARY KEY,gene_sz_name TEXT)",
            "INSERT INTO STA_lookup VALUES(2,'Chosen')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY,fkIDlookup INTEGER,note TEXT)",
            "INSERT INTO data VALUES(1,2,'original')").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        TypedRow row = await RowAsync(database, catalog).ConfigureAwait(false);
        RelationshipLookupOption choice = (await SearchAsync(database, catalog).ConfigureAwait(false)).Options.Single();
        await database.ExecuteAsync(change).ConfigureAwait(false);
        TypedRow beforeAttempt = await RowAsync(database, catalog).ConfigureAwait(false);
        var error = await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() =>
            _store.UpdateRowAsync(database.Path, catalog, Update(row, choice), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("fkIDlookup", error.ColumnName);
        Assert.AreEqual(beforeAttempt.Revision, (await RowAsync(database, catalog).ConfigureAwait(false)).Revision);
    }

    [TestMethod]
    public async Task AffinityCompatibleChoiceReportsNoChangeWhenStoredRowIsUnchanged()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER PRIMARY KEY,gene_sz_name TEXT)",
            "INSERT INTO STA_lookup VALUES(2,'Chosen')",
            "CREATE TABLE data(ID INTEGER PRIMARY KEY,fkIDlookup TEXT,note TEXT)",
            "INSERT INTO data VALUES(1,'2','original')").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await CatalogAsync(database).ConfigureAwait(false);
        TypedRow row = await RowAsync(database, catalog).ConfigureAwait(false);
        RelationshipLookupOption choice = (await SearchAsync(database, catalog).ConfigureAwait(false)).Options.Single();
        Assert.AreNotEqual(row.Values["fkIDlookup"], choice.Key);
        var result = await _store.UpdateRowAsync(database.Path, catalog, Update(row, choice), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, result.AffectedRows);
        Assert.AreEqual(SqliteValue.Text("2"), result.CurrentRow!.Values["fkIDlookup"]);
        Assert.AreEqual(row.Revision, result.CurrentRow.Revision);
    }

    private static Task<DatabaseSchemaCatalog> CatalogAsync(SqliteTestDatabase database) =>
        new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None);

    private Task<RelationshipLookupPage> SearchAsync(SqliteTestDatabase database, DatabaseSchemaCatalog catalog, string? search = null) =>
        _lookup.SearchAsync(database.Path, catalog, "data", "fkIDlookup", search, 100, CancellationToken.None);

    private Task<RelationshipLookupResult> ResolveAsync(SqliteTestDatabase database, DatabaseSchemaCatalog catalog, SqliteValue key) =>
        _lookup.ResolveAsync(database.Path, catalog, "data", "fkIDlookup", key, CancellationToken.None);

    private async Task<TypedRow> RowAsync(SqliteTestDatabase database, DatabaseSchemaCatalog catalog) =>
        (await _store.QueryRowsAsync(database.Path, catalog, new TableQuery("data", new PageRequest(0, 10)), CancellationToken.None)
            .ConfigureAwait(false)).Rows.Single();

    private static RowUpdateOperation Update(TypedRow row, RelationshipLookupOption option) =>
        new(Guid.NewGuid(), "data", DateTimeOffset.UtcNow, row.Identity!,
            [KeyValuePair.Create("fkIDlookup", row.Values["fkIDlookup"])],
            [KeyValuePair.Create("fkIDlookup", option.Key)], row.Revision,
            [KeyValuePair.Create("fkIDlookup", option)]);
}
