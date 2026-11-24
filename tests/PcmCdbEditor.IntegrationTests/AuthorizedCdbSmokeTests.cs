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
public sealed class AuthorizedCdbSmokeTests
{
    private static readonly JsonSerializerOptions ProofJsonOptions = new() { WriteIndented = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("AuthorizedLocalSmoke")]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task AuthorizedCdbsRoundTripOnlyThroughCopyFirstSessions()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PCM_CDB_AUTHORIZED_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive("Owner-authorized real CDB round trips were not enabled.");
        }

        string[] sourcePaths = new[]
            {
                Environment.GetEnvironmentVariable("PCM_CDB_SMOKE_1"),
                Environment.GetEnvironmentVariable("PCM_CDB_SMOKE_2")
            }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.HasCount(2, sourcePaths, "Two distinct owner-authorized CDB paths are required.");
        Assert.IsTrue(sourcePaths.All(path => Path.GetExtension(path).Equals(".cdb", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path)), "Both authorized paths must be existing CDB files.");

        string repositoryRoot = FindRepositoryRoot();
        string exporterPath = Path.Combine(repositoryRoot, "third_party", "SQLiteExporter", "SQLiteExporter.exe");
        Assert.IsTrue(File.Exists(exporterPath), "The approved repository exporter is required.");

        CancellationToken cancellationToken = TestContext.CancellationToken;
        var proofs = new List<SmokeProof>();
        for (var index = 0; index < sourcePaths.Length; index++)
        {
            int caseNumber = index + 1;
            string sourcePath = sourcePaths[index];
            await WithSmokeScratchAsync(async caseRoot =>
            {
                TestContext.WriteLine($"Case {caseNumber} disposable scratch: {caseRoot}");
                string copyPath = Path.Combine(caseRoot, "authorized-input.cdb");
                string sourceHashBefore;
                await using (var input = File.OpenRead(sourcePath))
                {
                    sourceHashBefore = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                    input.Position = 0;
                    await using var output = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
                try
                {
                    Assert.AreEqual(sourceHashBefore, await HashFileAsync(copyPath, cancellationToken).ConfigureAwait(false));
                    await RunOneAsync(copyPath, exporterPath, caseRoot, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    // Source integrity is useful even if the round trip fails or is cancelled.
                    using var integrityDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    Assert.AreEqual(sourceHashBefore, await HashFileAsync(sourcePath, integrityDeadline.Token).ConfigureAwait(false));
                }

                proofs.Add(new SmokeProof(caseNumber, true, true, true));
            }).ConfigureAwait(false);
        }

        TestContext.WriteLine($"Verified {proofs.Count} authorized CDB round trips and unchanged sources. Disposable copies were removed.");
        if (Environment.GetEnvironmentVariable("PCM_CDB_SMOKE_RETAIN_PROOF") == "1")
        {
            string proofRoot = Path.Combine(repositoryRoot, "local-smoke");
            Directory.CreateDirectory(proofRoot);
            string reportPath = Path.Combine(proofRoot, $"authorized-cdb-{Guid.NewGuid():N}.proof.json");
            var report = new
            {
                CompletedUtc = DateTimeOffset.UtcNow,
                ExporterSha256 = await HashFileAsync(exporterPath, cancellationToken).ConfigureAwait(false),
                Cases = proofs
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, ProofJsonOptions), cancellationToken)
                .ConfigureAwait(false);
            TestContext.WriteLine($"Retained redacted proof: {reportPath}");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SmokeScratchIsRemovedAfterSuccessAndCancellation(bool cancel)
    {
        string? scratch = null;
        Task UseScratchAsync() => WithSmokeScratchAsync(async root =>
        {
            scratch = root;
            await File.WriteAllTextAsync(Path.Combine(root, "synthetic-copy.cdb"), "synthetic").ConfigureAwait(false);
            if (cancel)
            {
                throw new OperationCanceledException();
            }
        });
        if (cancel)
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(UseScratchAsync).ConfigureAwait(false);
        }
        else
        {
            await UseScratchAsync().ConfigureAwait(false);
        }
        Assert.IsNotNull(scratch);
        Assert.IsFalse(Directory.Exists(scratch));
    }

    private static async Task WithSmokeScratchAsync(Func<string, Task> run)
    {
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("PcmCdbAuthorizedSmoke-");
        try
        {
            await run(scratch.FullName).ConfigureAwait(false);
        }
        finally
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(scratch.FullName, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(150).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task RunOneAsync(
        string sourcePath,
        string exporterPath,
        string caseRoot,
        CancellationToken cancellationToken)
    {
        string sourceHashBefore = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        string sessionsRoot = Path.Combine(caseRoot, "sessions");
        string backupsRoot = Path.Combine(caseRoot, "backups");
        string outputPath = Path.Combine(caseRoot, "roundtrip.cdb");
        using var workspace = new WorkspaceService(
            new CdbConverter(exporterPath),
            sessionsRoot,
            backupsRoot);

        EditorSessionState first = await workspace.OpenAsync(
                new WorkspaceOpenRequest(sourcePath),
                cancellationToken)
            .ConfigureAwait(false);
        SentinelTarget sentinel = await CreateSentinelAsync(first.WorkingSqlitePath, cancellationToken).ConfigureAwait(false);
        first = await workspace.MarkDirtyAsync(first, cancellationToken).ConfigureAwait(false);
        WorkspaceSaveResult firstSave = await workspace.SaveAsAsync(
                first,
                outputPath,
                cancellationToken)
            .ConfigureAwait(false);
        Assert.IsNull(firstSave.BackupPath);
        Assert.AreEqual(sourceHashBefore, await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false));
        await workspace.CloseAsync(firstSave.Session, discardDirtySession: false, cancellationToken)
            .ConfigureAwait(false);

        EditorSessionState second = await workspace.OpenAsync(
                new WorkspaceOpenRequest(outputPath),
                cancellationToken)
            .ConfigureAwait(false);
        TypedRow secondRow = await ReadSentinelRowAsync(second.WorkingSqlitePath, sentinel, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(sentinel.FirstValue, secondRow.Values[sentinel.ColumnName]);

        var store = new SqliteTableDataStore();
        var secondCatalog = await new SqliteTableCatalog()
            .DiscoverAsync(second.WorkingSqlitePath, cancellationToken)
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
                cancellationToken)
            .ConfigureAwait(false);
        second = await workspace.MarkDirtyAsync(second, cancellationToken).ConfigureAwait(false);
        WorkspaceSaveResult replacement = await workspace.SaveAsync(second, cancellationToken)
            .ConfigureAwait(false);
        Assert.IsNotNull(replacement.BackupPath);
        Assert.IsTrue(new FileInfo(replacement.BackupPath).Length > 0);
        await workspace.CloseAsync(replacement.Session, discardDirtySession: false, cancellationToken)
            .ConfigureAwait(false);

        EditorSessionState replaced = await workspace.OpenAsync(
                new WorkspaceOpenRequest(outputPath),
                cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(
            sentinel.SecondValue,
            (await ReadSentinelRowAsync(replaced.WorkingSqlitePath, sentinel, cancellationToken).ConfigureAwait(false))
                .Values[sentinel.ColumnName]);
        await workspace.CloseAsync(replaced, discardDirtySession: false, cancellationToken)
            .ConfigureAwait(false);

        EditorSessionState backup = await workspace.OpenAsync(
                new WorkspaceOpenRequest(replacement.BackupPath),
                cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(
            sentinel.FirstValue,
            (await ReadSentinelRowAsync(backup.WorkingSqlitePath, sentinel, cancellationToken).ConfigureAwait(false))
                .Values[sentinel.ColumnName]);
        await workspace.CloseAsync(backup, discardDirtySession: false, cancellationToken)
            .ConfigureAwait(false);

        Assert.AreEqual(sourceHashBefore, await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<SentinelTarget> CreateSentinelAsync(
        string sqlitePath, CancellationToken cancellationToken)
    {
        var catalog = await new SqliteTableCatalog()
            .DiscoverAsync(sqlitePath, cancellationToken)
            .ConfigureAwait(false);
        var store = new SqliteTableDataStore();
        SentinelTarget? knownTarget = await TryCreateKnownNumericSentinelAsync(
                sqlitePath,
                catalog,
                store,
                cancellationToken)
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
                    cancellationToken)
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
            await store.UpdateCellAsync(sqlitePath, catalog, operation, cancellationToken)
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
        SqliteTableDataStore store,
        CancellationToken cancellationToken)
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
                cancellationToken)
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
        await store.UpdateCellAsync(sqlitePath, catalog, operation, cancellationToken)
            .ConfigureAwait(false);
        return new SentinelTarget(tableName, columnName, row.Identity!, firstValue, secondValue);
    }

    private static async Task<TypedRow> ReadSentinelRowAsync(
        string sqlitePath, SentinelTarget target, CancellationToken cancellationToken)
    {
        var catalog = await new SqliteTableCatalog()
            .DiscoverAsync(sqlitePath, cancellationToken)
            .ConfigureAwait(false);
        TablePage page = await new SqliteTableDataStore().QueryAsync(
                sqlitePath,
                catalog,
                new TableQuery(target.TableName, new PageRequest(0, 100)),
                cancellationToken)
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

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private sealed record SmokeProof(
        int CaseNumber,
        bool SourcesUnchanged,
        bool OutputRoundTripVerified,
        bool ReplacementBackupVerified);

    private sealed record SentinelTarget(
        string TableName,
        string ColumnName,
        RowIdentity Identity,
        SqliteValue FirstValue,
        SqliteValue SecondValue);

    private sealed record SentinelCell(TypedRow Row, ColumnSchema Column, SqliteValue OldValue);
}
