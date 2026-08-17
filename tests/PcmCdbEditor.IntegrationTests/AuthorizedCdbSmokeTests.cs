using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Conversion;
using PcmCdbEditor.Infrastructure.Sqlite;
using PcmCdbEditor.Infrastructure.Workspace;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
[TestCategory("AuthorizedLocalSmoke")]
public sealed class AuthorizedCdbSmokeTests
{
    private static readonly JsonSerializerOptions ProofJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    [Timeout(1_200_000)]
    public async Task AuthorizedCdbsRoundTripOnlyThroughCopyFirstSessions()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PCM_CDB_AUTHORIZED_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            // The two real files and their local proof report are intentionally unavailable
            // in clean clones and CI. The synthetic integration suite remains the canonical gate.
            return;
        }

        var sourcePaths = new[]
            {
                Environment.GetEnvironmentVariable("PCM_CDB_SMOKE_1"),
                Environment.GetEnvironmentVariable("PCM_CDB_SMOKE_2")
            }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .ToArray();
        Assert.HasCount(2, sourcePaths, "Exactly two owner-authorized smoke CDB paths are required.");

        string repositoryRoot = FindRepositoryRoot();
        string exporterPath = Path.Combine(
            repositoryRoot,
            "third_party",
            "SQLiteExporter",
            "SQLiteExporter.exe");
        Assert.IsTrue(File.Exists(exporterPath), "The approved repository exporter is required.");

        string proofRoot = Path.Combine(repositoryRoot, "local-smoke", $"proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(proofRoot);
        var proofs = new List<SmokeProof>();
        foreach (string sourcePath in sourcePaths)
        {
            proofs.Add(await RunOneAsync(sourcePath, exporterPath, proofRoot).ConfigureAwait(false));
        }

        string reportPath = Path.Combine(proofRoot, "authorized-cdb-proof.json");
        await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(proofs, ProofJsonOptions))
            .ConfigureAwait(false);
        Assert.IsTrue(proofs.All(static proof => proof.Passed));
    }

    private static async Task<SmokeProof> RunOneAsync(
        string sourcePath,
        string exporterPath,
        string proofRoot)
    {
        string sourceHashBefore = HashFile(sourcePath);
        string caseRoot = Path.Combine(proofRoot, Guid.NewGuid().ToString("N"));
        string sessionsRoot = Path.Combine(caseRoot, "sessions");
        string backupsRoot = Path.Combine(caseRoot, "backups");
        string outputPath = Path.Combine(caseRoot, "roundtrip.cdb");
        var workspace = new WorkspaceService(
            new CdbConverter(exporterPath),
            sessionsRoot,
            backupsRoot);

        EditorSessionState first = await workspace.OpenAsync(
                new WorkspaceOpenRequest(sourcePath),
                CancellationToken.None)
            .ConfigureAwait(false);
        SentinelTarget sentinel = await CreateSentinelAsync(first.WorkingSqlitePath).ConfigureAwait(false);
        first = await workspace.MarkDirtyAsync(first, CancellationToken.None).ConfigureAwait(false);
        WorkspaceSaveResult firstSave = await workspace.SaveAsAsync(
                first,
                outputPath,
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNull(firstSave.BackupPath);
        Assert.AreEqual(sourceHashBefore, HashFile(sourcePath));
        await workspace.CloseAsync(firstSave.Session, discardDirtySession: false, CancellationToken.None)
            .ConfigureAwait(false);

        EditorSessionState second = await workspace.OpenAsync(
                new WorkspaceOpenRequest(outputPath),
                CancellationToken.None)
            .ConfigureAwait(false);
        TypedRow secondRow = await ReadSentinelRowAsync(second.WorkingSqlitePath, sentinel).ConfigureAwait(false);
        Assert.AreEqual(sentinel.FirstValue, secondRow.Values[sentinel.ColumnName]);

        var store = new SqliteTableDataStore();
        var secondCatalog = await new SqliteTableCatalog()
            .DiscoverAsync(second.WorkingSqlitePath, CancellationToken.None)
            .ConfigureAwait(false);
        var secondEdit = new CellUpdateOperation(
            Guid.NewGuid(),
            sentinel.TableName,
            DateTimeOffset.UtcNow,
            secondRow.Identity!,
            sentinel.ColumnName,
            secondRow.Values[sentinel.ColumnName],
            sentinel.SecondValue,
            secondRow.Revision);
        await store.UpdateCellAsync(
                second.WorkingSqlitePath,
                secondCatalog,
                secondEdit,
                CancellationToken.None)
            .ConfigureAwait(false);
        second = await workspace.MarkDirtyAsync(second, CancellationToken.None).ConfigureAwait(false);
        WorkspaceSaveResult replacement = await workspace.SaveAsync(second, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(replacement.BackupPath);
        Assert.IsTrue(new FileInfo(replacement.BackupPath).Length > 0);
        await workspace.CloseAsync(replacement.Session, discardDirtySession: false, CancellationToken.None)
            .ConfigureAwait(false);

        EditorSessionState replaced = await workspace.OpenAsync(
                new WorkspaceOpenRequest(outputPath),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(
            sentinel.SecondValue,
            (await ReadSentinelRowAsync(replaced.WorkingSqlitePath, sentinel).ConfigureAwait(false))
                .Values[sentinel.ColumnName]);
        await workspace.CloseAsync(replaced, discardDirtySession: false, CancellationToken.None)
            .ConfigureAwait(false);

        EditorSessionState backup = await workspace.OpenAsync(
                new WorkspaceOpenRequest(replacement.BackupPath),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(
            sentinel.FirstValue,
            (await ReadSentinelRowAsync(backup.WorkingSqlitePath, sentinel).ConfigureAwait(false))
                .Values[sentinel.ColumnName]);
        await workspace.CloseAsync(backup, discardDirtySession: false, CancellationToken.None)
            .ConfigureAwait(false);

        string sourceHashAfter = HashFile(sourcePath);
        Assert.AreEqual(sourceHashBefore, sourceHashAfter);
        return new SmokeProof(
            sourcePath,
            sourceHashBefore,
            sourceHashAfter,
            outputPath,
            replacement.BackupPath,
            Passed: true);
    }

    private static async Task<SentinelTarget> CreateSentinelAsync(string sqlitePath)
    {
        var catalog = await new SqliteTableCatalog()
            .DiscoverAsync(sqlitePath, CancellationToken.None)
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        SentinelTarget? knownTarget = await TryCreateKnownNumericSentinelAsync(
                sqlitePath,
                catalog,
                store)
            .ConfigureAwait(false);
        if (knownTarget is not null)
        {
            return knownTarget;
        }

        foreach (TableSchema table in catalog.Tables
                     .Where(static table => table.EditCapability == TableEditCapability.Editable)
                     .OrderBy(static table => table.StableIdentity.Kind == StableIdentityKind.DeclaredPrimaryKey ? 0 : 1)
                     .ThenBy(static table => table.Name, StringComparer.OrdinalIgnoreCase))
        {
            ColumnSchema[] writableColumns = table.Columns.Where(static column =>
                    !column.IsPrimaryKey
                    && !column.IsGenerated
                    && !column.IsHidden)
                .ToArray();
            if (writableColumns.Length == 0)
            {
                continue;
            }

            TablePage page = await store.QueryAsync(
                    sqlitePath,
                    catalog,
                    new TableQuery(table.Name, new PageRequest(0, 100)),
                    CancellationToken.None)
                .ConfigureAwait(false);
            SentinelCell? target = page.Rows
                .Where(static row => row.Identity is not null)
                .SelectMany(row => writableColumns
                    .Where(column => row.Values.TryGetValue(column.Name, out SqliteValue value)
                                     && value.Kind == SqliteValueKind.Text)
                    .Select(column => new SentinelCell(row, column, row.Values[column.Name])))
                .FirstOrDefault();
            if (target is null)
            {
                continue;
            }

            var firstValue = SqliteValue.Text($"smk{Guid.NewGuid():N}"[..12]);
            var secondValue = SqliteValue.Text($"smk{Guid.NewGuid():N}"[..12]);
            var operation = new CellUpdateOperation(
                Guid.NewGuid(),
                table.Name,
                DateTimeOffset.UtcNow,
                target.Row.Identity!,
                target.Column.Name,
                target.OldValue,
                firstValue,
                target.Row.Revision);
            await store.UpdateCellAsync(sqlitePath, catalog, operation, CancellationToken.None)
                .ConfigureAwait(false);
            return new SentinelTarget(
                table.Name,
                target.Column.Name,
                target.Row.Identity!,
                firstValue,
                secondValue);
        }

        throw new AssertFailedException("The authorized working copy has no safe editable text cell for the sentinel proof.");
    }

    private static async Task<SentinelTarget?> TryCreateKnownNumericSentinelAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        SqliteTableDataStore store)
    {
        const string tableName = "STA_country";
        const string columnName = "gene_i_num_cyclist_WC";
        if (!catalog.TryGetTable(tableName, out TableSchema table)
            || table.EditCapability != TableEditCapability.Editable
            || table.Columns.All(column => !column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        TablePage page = await store.QueryAsync(
                sqlitePath,
                catalog,
                new TableQuery(tableName, new PageRequest(0, 100)),
                CancellationToken.None)
            .ConfigureAwait(false);
        TypedRow? row = page.Rows.FirstOrDefault(candidate =>
            candidate.Identity is not null
            && candidate.Values.TryGetValue(columnName, out SqliteValue value)
            && value.Kind == SqliteValueKind.Integer);
        if (row is null)
        {
            return null;
        }

        SqliteValue oldValue = row.Values[columnName];
        SqliteValue firstValue = SqliteValue.Integer(oldValue.IntegerValue == 0 ? 1 : 0);
        SqliteValue secondValue = SqliteValue.Integer(firstValue.IntegerValue == 0 ? 1 : 0);
        var operation = new CellUpdateOperation(
            Guid.NewGuid(),
            tableName,
            DateTimeOffset.UtcNow,
            row.Identity!,
            columnName,
            oldValue,
            firstValue,
            row.Revision);
        await store.UpdateCellAsync(sqlitePath, catalog, operation, CancellationToken.None)
            .ConfigureAwait(false);
        return new SentinelTarget(tableName, columnName, row.Identity!, firstValue, secondValue);
    }

    private static async Task<TypedRow> ReadSentinelRowAsync(string sqlitePath, SentinelTarget target)
    {
        var catalog = await new SqliteTableCatalog()
            .DiscoverAsync(sqlitePath, CancellationToken.None)
            .ConfigureAwait(false);
        TablePage page = await new SqliteTableDataStore().QueryAsync(
                sqlitePath,
                catalog,
                new TableQuery(target.TableName, new PageRequest(0, 100)),
                CancellationToken.None)
            .ConfigureAwait(false);
        TypedRow[] matchingRows = page.Rows
            .Where(row => target.Identity.Equals(row.Identity))
            .ToArray();
        Assert.HasCount(1, matchingRows, "The sentinel row identity did not survive the CDB round trip.");
        return matchingRows[0];
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PcmCdbEditor.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root could not be resolved safely.");
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record SmokeProof(
        string SourcePath,
        string SourceHashBefore,
        string SourceHashAfter,
        string OutputPath,
        string BackupPath,
        bool Passed);

    private sealed record SentinelTarget(
        string TableName,
        string ColumnName,
        RowIdentity Identity,
        SqliteValue FirstValue,
        SqliteValue SecondValue);

    private sealed record SentinelCell(TypedRow Row, ColumnSchema Column, SqliteValue OldValue);
}
