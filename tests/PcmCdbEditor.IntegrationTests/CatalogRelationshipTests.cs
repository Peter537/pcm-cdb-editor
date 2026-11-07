using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class CatalogRelationshipTests
{
    private const string TeamSchema = "CREATE TABLE DYN_team(IDteam INTEGER PRIMARY KEY, gene_sz_name TEXT)";
    private const string ContractTable = "DYN_contract_cyclist";
    private const string PreviousTeamColumn = "fkIDprevteam";

    [TestMethod]
    [DataRow("CREATE TABLE notes(value TEXT)", null)]
    [DataRow("CREATE TABLE STA_team(IDteam INTEGER, name TEXT)", "STA_team")]
    [DataRow("CREATE TABLE DYN_prevteam(IDprevteam INTEGER, name TEXT)", "DYN_prevteam")]
    public async Task PreviousTeamExceptionRequiresOneUncontestedTarget(string additionalSchema, string? competingTable)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            TeamSchema,
            "CREATE TABLE DYN_contract_cyclist(IDcontract_cyclist INTEGER PRIMARY KEY, fkIDprevteam INTEGER)",
            additionalSchema).ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await DiscoverAsync(database).ConfigureAwait(false);
        Assert.IsTrue(catalog.TryGetTable(ContractTable, out TableSchema contract));
        if (competingTable is null)
        {
            Assert.IsTrue(RelationshipMetadata.TryGetLabelRelation(
                catalog, contract, PreviousTeamColumn, out ForeignKeyRelation relationship));
            Assert.AreEqual("DYN_team", relationship.TargetTable);
            Assert.AreEqual("IDteam", relationship.TargetColumn);
            Assert.AreEqual("gene_sz_name", relationship.DisplayColumn);
            Assert.IsFalse(relationship.IsDeclared);
        }
        else
        {
            AssertAmbiguousCandidates(catalog, contract, PreviousTeamColumn, "DYN_team", competingTable);
        }
    }

    [TestMethod]
    public async Task TwoInferredTargetsRemainAmbiguousAndPreserveTheRawKey()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            """
            CREATE TABLE DYN_entry(IDentry INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE STA_entry(IDentry INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE DYN_source(IDrow INTEGER PRIMARY KEY, fkIDentry INTEGER);
            INSERT INTO DYN_entry VALUES(7, 'Dynamic label');
            INSERT INTO STA_entry VALUES(7, 'Static label');
            INSERT INTO DYN_source VALUES(1, 7);
            """).ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await DiscoverAsync(database).ConfigureAwait(false);
        Assert.IsTrue(catalog.TryGetTable("DYN_source", out TableSchema source));
        AssertAmbiguousCandidates(catalog, source, "fkIDentry", "DYN_entry", "STA_entry");

        var query = new TableQuery(source.Name, new PageRequest(0, 10),
            foreignKeyDisplayMode: ForeignKeyDisplayMode.ResolvedName);
        var page = await new SqliteTableDataStore().QueryAsync(database.Path, catalog, query, CancellationToken.None)
            .ConfigureAwait(false);
        TypedRow row = Assert.ContainsSingle(page.Rows);
        Assert.AreEqual(SqliteValue.Integer(7), row.Values["fkIDentry"]);
        Assert.AreEqual(RelationshipResolution.AmbiguousRelationship, row.RelatedValues["fkIDentry"].Resolution);
        Assert.IsNull(row.RelatedValues["fkIDentry"].Label);
    }

    [TestMethod]
    [DataRow("CREATE TABLE STA_team(IDteam INTEGER PRIMARY KEY, name TEXT)", true)]
    [DataRow("CREATE TABLE notes(value TEXT)", false)]
    public async Task PreviousTeamDeclaredTargetTakesPrecedenceEvenWhenUnavailable(string additionalSchema, bool targetExists)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            TeamSchema,
            "CREATE TABLE DYN_contract_cyclist(fkIDprevteam INTEGER REFERENCES sta_TEAM(IDTEAM))",
            additionalSchema).ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await DiscoverAsync(database).ConfigureAwait(false);
        Assert.IsTrue(catalog.TryGetTable(ContractTable, out TableSchema contract));
        ForeignKeyRelation relationship = Assert.ContainsSingle(contract.Relationships);
        Assert.IsTrue(relationship.IsDeclared);
        Assert.IsFalse(relationship.IsComposite);
        Assert.AreEqual(targetExists, RelationshipMetadata.TryGetLabelRelation(
            catalog, contract, PreviousTeamColumn, out _));
        Assert.AreEqual(targetExists ? "STA_team" : "sta_TEAM", relationship.TargetTable);
        Assert.AreEqual(targetExists ? "IDteam" : "IDTEAM", relationship.TargetColumn);
    }

    [TestMethod]
    [DataRow("(IDteam, season)", true)]
    [DataRow("", true)]
    [DataRow("", false)]
    public async Task DeclaredCompositeComponentsRemainRawEvenWhenTheTupleTargetIsUnresolved(
        string targetColumns,
        bool targetExists)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            TeamSchema,
            targetExists
                ? "CREATE TABLE old_team(IDteam INTEGER, season INTEGER, name TEXT, PRIMARY KEY(IDteam, season))"
                : "CREATE TABLE notes(value TEXT)",
            $"""
            CREATE TABLE DYN_contract_cyclist(
                fkIDprevteam INTEGER,
                season INTEGER,
                FOREIGN KEY(fkIDprevteam, season) REFERENCES old_team{targetColumns})
            """).ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await DiscoverAsync(database).ConfigureAwait(false);
        Assert.IsTrue(catalog.TryGetTable(ContractTable, out TableSchema contract));
        Assert.HasCount(2, contract.Relationships);
        foreach (ForeignKeyRelation component in contract.Relationships)
        {
            Assert.IsTrue(component.IsDeclared);
            Assert.IsTrue(component.IsComposite);
            Assert.AreEqual("old_team", component.TargetTable);
            Assert.IsNull(component.DisplayColumn);
            Assert.IsFalse(RelationshipMetadata.TryGetLabelRelation(catalog, contract, component.SourceColumn, out _));
        }
    }

    [TestMethod]
    public async Task MultipleDeclarationsForOneColumnHaveNoUnambiguousLabelRelation()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            TeamSchema,
            "CREATE TABLE STA_team(IDteam INTEGER PRIMARY KEY, name TEXT)",
            """
            CREATE TABLE DYN_contract_cyclist(
                fkIDprevteam INTEGER REFERENCES DYN_team(IDteam),
                FOREIGN KEY(fkIDprevteam) REFERENCES STA_team(IDteam))
            """).ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await DiscoverAsync(database).ConfigureAwait(false);
        Assert.IsTrue(catalog.TryGetTable(ContractTable, out TableSchema contract));
        Assert.HasCount(2, contract.Relationships);
        Assert.IsTrue(contract.Relationships.All(static relation => relation.IsDeclared && !relation.IsComposite));
        Assert.IsFalse(RelationshipMetadata.TryGetLabelRelation(catalog, contract, PreviousTeamColumn, out _));
    }

    [TestMethod]
    public async Task RiderLabelUsesBothNamesAndExplicitConfigurationCanChooseAnotherColumn()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            """
            CREATE TABLE DYN_cyclist(IDcyclist INTEGER PRIMARY KEY, notes TEXT,
                gene_sz_firstlastname TEXT, gene_sz_lastname TEXT, gene_sz_firstname TEXT)
            """,
            "CREATE TABLE DYN_contract_cyclist(fkIDcyclist INTEGER REFERENCES dyn_CYCLIST)")
            .ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await DiscoverAsync(database).ConfigureAwait(false);
        Assert.IsTrue(catalog.TryGetTable(ContractTable, out TableSchema contract));
        Assert.IsTrue(RelationshipMetadata.TryGetLabelRelation(catalog, contract, "FKIDCYCLIST", out var relationship));
        Assert.AreEqual("fkIDcyclist", relationship.SourceColumn);
        Assert.AreEqual("DYN_cyclist", relationship.TargetTable);
        Assert.AreEqual("IDcyclist", relationship.TargetColumn);
        Assert.AreEqual("gene_sz_firstname", relationship.DisplayColumn);
        Assert.AreEqual("gene_sz_lastname", relationship.AdditionalDisplayColumn);

        var configured = new SqliteTableCatalog(new Dictionary<string, string> { ["dyn_cyclist"] = "NOTES" });
        DatabaseSchemaCatalog configuredCatalog = await configured.DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsTrue(configuredCatalog.TryGetTable(ContractTable, out TableSchema configuredContract));
        Assert.IsTrue(RelationshipMetadata.TryGetLabelRelation(
            configuredCatalog, configuredContract, "fkIDcyclist", out var configuredRelation));
        Assert.AreEqual("notes", configuredRelation.DisplayColumn);
        Assert.IsNull(configuredRelation.AdditionalDisplayColumn);
    }

    [TestMethod]
    public async Task OnlyRecognizedLabelColumnsAreChosenAutomatically()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_race(IDrace INTEGER PRIMARY KEY, notes TEXT, gene_sz_race_name TEXT)",
            "CREATE TABLE STA_entry(IDentry INTEGER PRIMARY KEY, notes TEXT)",
            "CREATE TABLE DYN_stage(fkIDrace INTEGER, fkIDentry INTEGER)").ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await DiscoverAsync(database).ConfigureAwait(false);
        Assert.IsTrue(catalog.TryGetTable("DYN_stage", out TableSchema stage));
        Assert.IsTrue(RelationshipMetadata.TryGetLabelRelation(catalog, stage, "fkIDrace", out var race));
        Assert.AreEqual("gene_sz_race_name", race.DisplayColumn);
        ForeignKeyRelation entry = stage.Relationships.Single(static relation => relation.SourceColumn == "fkIDentry");
        Assert.AreEqual("STA_entry", entry.TargetTable);
        Assert.IsNull(entry.DisplayColumn);
        Assert.IsFalse(RelationshipMetadata.TryGetLabelRelation(catalog, stage, "fkIDentry", out _));
    }

    [TestMethod]
    public async Task ExistingSeasonResultMappingKeepsItsConfiguredTarget()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_result_season(fkIDresult_season_team INTEGER)",
            "CREATE TABLE DYN_result_season_team(IDresult_season_team INTEGER PRIMARY KEY, name TEXT)",
            "CREATE TABLE STA_result_season_team(IDresult_season_team INTEGER PRIMARY KEY, name TEXT)")
            .ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await DiscoverAsync(database).ConfigureAwait(false);
        Assert.IsTrue(catalog.TryGetTable("DYN_result_season", out TableSchema result));
        Assert.IsTrue(RelationshipMetadata.TryGetLabelRelation(
            catalog, result, "fkIDresult_season_team", out var relationship));
        Assert.AreEqual("DYN_result_season_team", relationship.TargetTable);
    }

    private static Task<DatabaseSchemaCatalog> DiscoverAsync(SqliteTestDatabase database) =>
        new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None);

    private static void AssertAmbiguousCandidates(
        DatabaseSchemaCatalog catalog,
        TableSchema source,
        string columnName,
        string firstTarget,
        string secondTarget)
    {
        Assert.IsFalse(RelationshipMetadata.TryGetLabelRelation(catalog, source, columnName, out _));
        CollectionAssert.AreEquivalent(
            new[] { firstTarget, secondTarget },
            source.Relationships.Select(static relation => relation.TargetTable).ToArray());
        foreach (ForeignKeyRelation candidate in source.Relationships)
        {
            Assert.AreEqual(columnName, candidate.SourceColumn);
            Assert.IsFalse(candidate.IsDeclared);
            Assert.IsNull(candidate.DisplayColumn);
            Assert.IsNull(candidate.AdditionalDisplayColumn);
        }
    }
}
