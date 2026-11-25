using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Infrastructure.Conversion;
using PcmCdbEditor.Infrastructure.Sqlite;
using PcmCdbEditor.Infrastructure.Workspace;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class CdbConverterTests
{
    private const int DiagnosticCharacterLimit = 16 * 1024;
    private static string s_fixtureBuildRoot = string.Empty;
    private static string s_fakeConverterPath = string.Empty;

    [ClassInitialize]
    public static async Task CompileFakeConverterAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        s_fixtureBuildRoot = Path.Combine(
            Path.GetTempPath(),
            "PcmCdbEditorTests",
            "FakeConverter",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(s_fixtureBuildRoot);
        s_fakeConverterPath = Path.Combine(s_fixtureBuildRoot, "FakeConverter.exe");
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "FakeConverterProgram.cs");
        var compilerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET",
            "Framework64",
            "v4.0.30319",
            "csc.exe");
        Assert.IsTrue(File.Exists(sourcePath), $"Missing fake converter source: {sourcePath}");
        Assert.IsTrue(File.Exists(compilerPath), $"Missing Windows C# compiler: {compilerPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = compilerPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("/nologo");
        startInfo.ArgumentList.Add("/target:exe");
        startInfo.ArgumentList.Add("/optimize+");
        startInfo.ArgumentList.Add($"/out:{s_fakeConverterPath}");
        startInfo.ArgumentList.Add(sourcePath);

        using var compiler = new Process { StartInfo = startInfo };
        Assert.IsTrue(compiler.Start(), "The local fake-converter compiler did not start.");
        var standardOutput = compiler.StandardOutput.ReadToEndAsync();
        var standardError = compiler.StandardError.ReadToEndAsync();
        await compiler.WaitForExitAsync().ConfigureAwait(false);
        var diagnostics = (await standardOutput.ConfigureAwait(false))
            + (await standardError.ConfigureAwait(false));
        Assert.AreEqual(0, compiler.ExitCode, diagnostics);
        Assert.IsTrue(File.Exists(s_fakeConverterPath), "The fake converter executable was not produced.");
    }

    [ClassCleanup]
    public static void DeleteFakeConverter()
    {
        TryDeleteDirectory(s_fixtureBuildRoot);
    }

    [TestMethod]
    public async Task MissingExecutableAndInvalidInputHaveStableCategories()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "missing")
            .ConfigureAwait(false);
        var missingExecutable = Path.Combine(fixture.Root, "missing-exporter.exe");
        var converter = new CdbConverter(missingExecutable);
        var missing = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
            converter.ExportToSqliteAsync(fixture.InputPath, TimeSpan.FromMinutes(1), CancellationToken.None))
            .ConfigureAwait(false);
        Assert.AreEqual(ConverterFailureCategory.MissingExecutable, missing.Failure.Category);
        Assert.IsFalse(missing.Message.Contains(fixture.Root, StringComparison.OrdinalIgnoreCase));

        var invalid = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
            converter.ExportToSqliteAsync(
                Path.Combine(fixture.Root, "wrong.sqlite"),
                TimeSpan.FromMinutes(1),
                CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ConverterFailureCategory.InvalidInput, invalid.Failure.Category);
    }

    [TestMethod]
    public async Task PreCancelledOperationDoesNotStartConverter()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "sleep")
            .ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var converter = new CdbConverter(fixture.ExecutablePath);
        var exception = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
            converter.ExportToSqliteAsync(
                fixture.InputPath,
                TimeSpan.FromMinutes(1),
                cancellation.Token)).ConfigureAwait(false);

        Assert.AreEqual(ConverterFailureCategory.Cancelled, exception.Failure.Category);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "parent.pid")));
    }

    [TestMethod]
    public async Task NonZeroExitIncludesStructuredBoundedDiagnostics()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "nonzero")
            .ConfigureAwait(false);
        var converter = new CdbConverter(fixture.ExecutablePath);

        var exception = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
            converter.ExportToSqliteAsync(
                fixture.InputPath,
                TimeSpan.FromMinutes(1),
                CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual(ConverterFailureCategory.NonZeroExit, exception.Failure.Category);
        var diagnostics = exception.Failure.Diagnostics
            ?? throw new AssertFailedException("Nonzero exit diagnostics were missing.");
        Assert.AreEqual(23, diagnostics.ExitCode);
        Assert.AreEqual("neutral standard output", diagnostics.StandardOutput);
        Assert.AreEqual("neutral standard error", diagnostics.StandardError);
    }

    [TestMethod]
    public async Task InvalidExecutableHasStartFailureCategory()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "missing")
            .ConfigureAwait(false);
        var invalidExecutable = Path.Combine(fixture.Root, "invalid.exe");
        await File.WriteAllTextAsync(invalidExecutable, "not a Windows executable").ConfigureAwait(false);
        var converter = new CdbConverter(invalidExecutable);

        var exception = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
            converter.ExportToSqliteAsync(
                fixture.InputPath,
                TimeSpan.FromMinutes(1),
                CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual(ConverterFailureCategory.StartFailure, exception.Failure.Category);
    }

    [TestMethod]
    public async Task SuccessfulExitRequiresPresentNonEmptyOutput()
    {
        await AssertOutputFailureAsync("missing", ConverterFailureCategory.MissingOutput)
            .ConfigureAwait(false);
        await AssertOutputFailureAsync("empty", ConverterFailureCategory.EmptyOutput)
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DiagnosticsAreDrainedBoundedSanitizedAndControlCharacterSafe()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "noise")
            .ConfigureAwait(false);
        var converter = new CdbConverter(fixture.ExecutablePath);

        var exception = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
            converter.ExportToSqliteAsync(
                fixture.InputPath,
                TimeSpan.FromMinutes(1),
                CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual(ConverterFailureCategory.NonZeroExit, exception.Failure.Category);
        var diagnostics = exception.Failure.Diagnostics
            ?? throw new AssertFailedException("Noisy process diagnostics were missing.");
        AssertBoundedAndSanitized(diagnostics.StandardOutput, fixture);
        AssertBoundedAndSanitized(diagnostics.StandardError, fixture);
        StringAssert.Contains(
            diagnostics.StandardOutput,
            "[diagnostic output truncated]",
            StringComparison.Ordinal);
        StringAssert.Contains(
            diagnostics.StandardError,
            "[diagnostic output truncated]",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SensitivePathsCrossingTheDiagnosticBoundaryAreFullyRedacted()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "boundary-noise")
            .ConfigureAwait(false);
        var converter = new CdbConverter(fixture.ExecutablePath);

        var exception = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
            converter.ExportToSqliteAsync(
                fixture.InputPath,
                TimeSpan.FromMinutes(1),
                CancellationToken.None)).ConfigureAwait(false);

        var diagnostics = exception.Failure.Diagnostics
            ?? throw new AssertFailedException("Boundary diagnostics were missing.");
        AssertBoundedAndSanitized(diagnostics.StandardOutput, fixture);
        AssertBoundedAndSanitized(diagnostics.StandardError, fixture);
        var pathPrefix = Path.GetPathRoot(fixture.Root)
            ?? throw new AssertFailedException("The fixture root had no Windows path prefix.");
        Assert.IsFalse(diagnostics.StandardOutput.Contains(pathPrefix, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(diagnostics.StandardError.Contains(pathPrefix, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ActiveCancellationTerminatesTheRunningProcess()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "sleep")
            .ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var converter = new CdbConverter(fixture.ExecutablePath);
        var conversion = converter.ExportToSqliteAsync(
            fixture.InputPath,
            TimeSpan.FromMinutes(1),
            cancellation.Token);
        try
        {
            var processId = await ReadProcessIdAsync(Path.Combine(fixture.Root, "parent.pid"))
                .ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            cancellation.Cancel();

            var exception = await Assert.ThrowsExactlyAsync<CdbConversionException>(() => conversion)
                .ConfigureAwait(false);

            Assert.AreEqual(ConverterFailureCategory.Cancelled, exception.Failure.Category);
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Active cancellation was not bounded.");
            await AssertProcessExitedAsync(processId).ConfigureAwait(false);
        }
        finally
        {
            await CancelAndObserveAsync(cancellation, conversion).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task TimeoutTerminatesTheRunningConverterAfterStartup()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "sleep")
            .ConfigureAwait(false);
        var converter = new CdbConverter(fixture.ExecutablePath);
        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        Task<ConversionResult> conversion = converter.ExportToSqliteAsync(
            fixture.InputPath, TimeSpan.FromSeconds(3), cancellation.Token);
        Process? process = null;
        try
        {
            int processId = await ReadProcessIdAsync(Path.Combine(fixture.Root, "parent.pid")).ConfigureAwait(false);
            process = OpenRunningFixtureProcess(processId, fixture.ExecutablePath);
            var exception = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
                conversion.WaitAsync(TimeSpan.FromSeconds(7))).ConfigureAwait(false);

            Assert.AreEqual(ConverterFailureCategory.TimedOut, exception.Failure.Category);
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(8), "The timeout path was not bounded.");
            await AssertProcessExitedAsync(processId).ConfigureAwait(false);
        }
        finally
        {
            await StopFixtureProcessAsync(cancellation, conversion, process).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CancellationTerminatesTheConverterChildProcessTree()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "spawn-child")
            .ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var converter = new CdbConverter(fixture.ExecutablePath);
        var conversion = converter.ExportToSqliteAsync(
            fixture.InputPath,
            TimeSpan.FromMinutes(1),
            cancellation.Token);
        try
        {
            var childProcessId = await ReadProcessIdAsync(Path.Combine(fixture.Root, "child.pid"))
                .ConfigureAwait(false);
            cancellation.Cancel();

            var exception = await Assert.ThrowsExactlyAsync<CdbConversionException>(() => conversion)
                .ConfigureAwait(false);

            Assert.AreEqual(ConverterFailureCategory.Cancelled, exception.Failure.Category);
            await AssertProcessExitedAsync(childProcessId).ConfigureAwait(false);
        }
        finally
        {
            await CancelAndObserveAsync(cancellation, conversion).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ConverterDeadlineOwnsPipesAfterTheParentExits()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "exit-after-child")
            .ConfigureAwait(false);
        var deadline = new ControlledDeadline();
        var converter = new CdbConverter(fixture.ExecutablePath, deadline);
        using var cancellation = new CancellationTokenSource();
        Task<ConversionResult> conversion = converter.ExportToSqliteAsync(
            fixture.InputPath, TimeSpan.FromSeconds(30), cancellation.Token);
        Process? child = null;
        try
        {
            int childId = await ReadProcessIdAsync(Path.Combine(fixture.Root, "child.pid"), TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            child = OpenRunningFixtureProcess(childId, fixture.ExecutablePath);
            Assert.IsNotNull(child, "The fixture descendant must remain alive until the deadline.");
            int parentId = await ReadProcessIdAsync(Path.Combine(fixture.Root, "parent.pid")).ConfigureAwait(false);
            await AssertProcessExitedAsync(parentId).ConfigureAwait(false);
            Assert.IsFalse(child.HasExited, "The descendant must still own the pipe after root exit.");
            Assert.IsFalse(conversion.IsCompleted, "The deadline must still own the descendant-held pipe.");
            var elapsed = Stopwatch.StartNew();
            await deadline.ExpireAsync().ConfigureAwait(false);
            var failure = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
                conversion.WaitAsync(TimeSpan.FromSeconds(7))).ConfigureAwait(false);
            Assert.AreEqual(ConverterFailureCategory.TimedOut, failure.Failure.Category);
            var diagnostics = failure.Failure.Diagnostics
                ?? throw new AssertFailedException("Timeout diagnostics were missing.");
            StringAssert.Contains(diagnostics.StandardOutput, "child started", StringComparison.Ordinal);
            AssertBoundedAndSanitized(diagnostics.StandardOutput, fixture);
            AssertBoundedAndSanitized(diagnostics.StandardError, fixture);
            await AssertProcessExitedAsync(childId).ConfigureAwait(false);
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(8));
        }
        finally
        {
            await StopFixtureProcessAsync(cancellation, conversion, child).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ArgumentListPreservesSpacesAndShellMetacharactersLiterally()
    {
        const string hostileButValidName = "space & () ^ ! % ; []";
        using var exportFixture = await ConverterFixture.CreateAsync(
                s_fakeConverterPath,
                "validate",
                Path.Combine(hostileButValidName, "in & echo no.cdb"))
            .ConfigureAwait(false);
        await exportFixture.ConfigureAsync(
                "validate",
                "-export",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(exportFixture.InputPath)))
            .ConfigureAwait(false);
        var exportConverter = new CdbConverter(exportFixture.ExecutablePath);

        ConversionResult export;
        try
        {
            export = await exportConverter.ExportToSqliteAsync(exportFixture.InputPath,
                TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        }
        catch (CdbConversionException failure)
        {
            throw new AssertFailedException(
                $"The argument fixture exited with {failure.Failure.Diagnostics?.ExitCode}. " +
                failure.Failure.Diagnostics?.StandardError, failure);
        }

        Assert.AreEqual(Path.ChangeExtension(exportFixture.InputPath, ".sqlite"), export.OutputPath);
        Assert.AreEqual(
            "synthetic converter output",
            await File.ReadAllTextAsync(export.OutputPath).ConfigureAwait(false));

        using var importFixture = await ConverterFixture.CreateAsync(
                s_fakeConverterPath,
                "validate",
                Path.Combine(hostileButValidName, "work & literal.sqlite"))
            .ConfigureAwait(false);
        File.Delete(importFixture.InputPath);
        await using (var db = SqliteSupport.CreateConnection(importFixture.InputPath, SqliteOpenMode.ReadWriteCreate))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE fixture (id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
        }
        var destination = Path.Combine(importFixture.Root, hostileButValidName, "save & (copy).cdb");
        var expectedBasePath = Path.Combine(
            Path.GetDirectoryName(destination)!,
            Path.GetFileNameWithoutExtension(destination));
        await importFixture.ConfigureAsync(
                "validate",
                "-import",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(expectedBasePath)))
            .ConfigureAwait(false);
        var importConverter = new CdbConverter(importFixture.ExecutablePath);

        var import = await importConverter.ImportToCdbAsync(
                importFixture.InputPath,
                destination,
                TimeSpan.FromMinutes(1),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(destination, import.OutputPath);
        Assert.AreEqual(
            "synthetic converter output",
            await File.ReadAllTextAsync(destination).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(expectedBasePath + ".sqlite"), "The import staging copy was not removed.");
    }

    [TestMethod]
    public async Task ImportIncludesCommittedPagesPinnedByAnOlderWalReader()
    {
        await using var working = await SqliteTestDatabase.CreateAsync(
            "PRAGMA journal_mode = WAL", "CREATE TABLE sample(value INTEGER)", "INSERT INTO sample VALUES(1)");
        await using var reader = SqliteSupport.CreateConnection(working.Path, SqliteOpenMode.ReadOnly);
        reader.Open();
        using var readTransaction = reader.BeginTransaction(deferred: true);
        using var original = reader.CreateCommand();
        original.Transaction = readTransaction;
        original.CommandText = "SELECT value FROM sample";
        Assert.AreEqual(1L, original.ExecuteScalar());
        await working.ExecuteAsync("UPDATE sample SET value = 2");

        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "copy-snapshot");
        string output = Path.Combine(fixture.Root, "staged.cdb");
        await new CdbConverter(fixture.ExecutablePath).ImportToCdbAsync(
            working.Path, output, TimeSpan.FromSeconds(20), CancellationToken.None);
        using var staged = SqliteSupport.CreateConnection(output, SqliteOpenMode.ReadOnly);
        staged.Open();
        using var published = staged.CreateCommand();
        published.CommandText = "SELECT value FROM sample";
        Assert.AreEqual(2L, published.ExecuteScalar());
        Assert.AreEqual(1L, original.ExecuteScalar());
        Assert.IsFalse(File.Exists(Path.ChangeExtension(output, ".sqlite")));
    }

    [TestMethod]
    public async Task TenMinuteMaximumAndWorkspaceDefaultAreEnforced()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(10), CdbConverter.MaximumTimeout);
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "neutral.cdb");
        await File.WriteAllTextAsync(source, "not-empty").ConfigureAwait(false);
        var capturingConverter = new TimeoutCapturingConverter();
        var service = new WorkspaceService(
            capturingConverter,
            Path.Combine(root, "sessions"),
            Path.Combine(root, "backups"));
        try
        {
            var session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(CdbConverter.MaximumTimeout, capturingConverter.ExportTimeout);
            await service.CloseAsync(session, discardDirtySession: false, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new WorkspaceService(
                capturingConverter,
                Path.Combine(root, "other-sessions"),
                Path.Combine(root, "other-backups"),
                CdbConverter.MaximumTimeout + TimeSpan.FromMilliseconds(1)));
        }
        finally
        {
            service.Dispose();
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task TimeoutMustBePositiveAndRequestsAboveTheCapRemainSafe()
    {
        var validateTimeout = typeof(CdbConverter).GetMethod(
            "ValidateTimeout",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("CdbConverter timeout validation was not found.");
        var clamped = validateTimeout.Invoke(
            null,
            [CdbConverter.MaximumTimeout + TimeSpan.FromDays(1)]);
        Assert.AreEqual(CdbConverter.MaximumTimeout, clamped);

        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "validate")
            .ConfigureAwait(false);
        await fixture.ConfigureAsync(
                "validate",
                "-export",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture.InputPath)))
            .ConfigureAwait(false);
        var converter = new CdbConverter(fixture.ExecutablePath);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            converter.ExportToSqliteAsync(fixture.InputPath, TimeSpan.Zero, CancellationToken.None))
            .ConfigureAwait(false);
        var result = await converter.ExportToSqliteAsync(
                fixture.InputPath,
                CdbConverter.MaximumTimeout + TimeSpan.FromDays(1),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsTrue(File.Exists(result.OutputPath));
    }

    private static async Task CancelAndObserveAsync(
        CancellationTokenSource cancellation,
        Task conversion)
    {
        if (conversion.IsCompleted)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            await conversion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CdbConversionException or TimeoutException)
        {
            // Cleanup observes the expected cancellation failure or bounds an abnormal fixture failure.
        }
    }

    private static async Task StopFixtureProcessAsync(
        CancellationTokenSource cancellation, Task conversion, Process? process)
    {
        using (process)
        {
            try
            {
                await CancelAndObserveAsync(cancellation, conversion).ConfigureAwait(false);
            }
            finally
            {
                if (process is { HasExited: false })
                {
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task AssertOutputFailureAsync(
        string scenario,
        ConverterFailureCategory expectedCategory)
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, scenario)
            .ConfigureAwait(false);
        var converter = new CdbConverter(fixture.ExecutablePath);
        var exception = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
            converter.ExportToSqliteAsync(
                fixture.InputPath,
                TimeSpan.FromMinutes(1),
                CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(expectedCategory, exception.Failure.Category);
        Assert.IsFalse(File.Exists(Path.ChangeExtension(fixture.InputPath, ".sqlite")));
    }

    private static void AssertBoundedAndSanitized(string value, ConverterFixture fixture)
    {
        Assert.IsTrue(
            value.Length <= DiagnosticCharacterLimit + 64,
            $"Diagnostic output exceeded its bound: {value.Length} characters.");
        Assert.IsFalse(value.Contains(fixture.Root, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(value.Contains(fixture.InputPath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(value.Contains(fixture.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(value.Any(static character =>
            char.IsControl(character) && character is not ('\r' or '\n' or '\t')));
    }

    private static async Task<int> ReadProcessIdAsync(string path, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(5);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < limit)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                    if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
                    {
                        return processId;
                    }
                }
            }
            catch (IOException)
            {
                // The fixture writes atomically enough for a bounded retry to observe the complete PID.
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new AssertFailedException($"The fake converter did not create {path} within {limit.TotalSeconds} seconds.");
    }

    private static Process? OpenRunningFixtureProcess(int processId, string expectedPath)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            if (!process.HasExited &&
                string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                return process;
            }
        }
        catch (Exception exception) when ((exception is Win32Exception or InvalidOperationException) && process.HasExited)
        {
            // The real deadline can expire while the fixture handle is being opened.
        }
        catch
        {
            process.Dispose();
            throw;
        }

        process.Dispose();
        return null;
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        Assert.Fail($"Process {processId} survived converter termination.");
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A failed assertion can briefly leave a process handle open; the temp root remains ignored.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed assertion can briefly leave a process handle open; the temp root remains ignored.
        }
    }

    private sealed class ControlledDeadline : TimeProvider
    {
        private readonly TaskCompletionSource<ITimer> _timer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.IsGreaterThan(TimeSpan.Zero, dueTime);
            Assert.AreEqual(Timeout.InfiniteTimeSpan, period);
            ITimer timer = TimeProvider.System.CreateTimer(
                callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.SetResult(timer);
            return timer;
        }

        public async Task ExpireAsync()
        {
            ITimer timer = await _timer.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsTrue(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        }
    }

    private sealed class ConverterFixture : IDisposable
    {
        private ConverterFixture(string root, string executablePath, string inputPath)
        {
            Root = root;
            ExecutablePath = executablePath;
            InputPath = inputPath;
        }

        public string Root { get; }

        public string ExecutablePath { get; }

        public string InputPath { get; }

        public static async Task<ConverterFixture> CreateAsync(
            string compiledFakeConverter,
            string scenario,
            string? relativeInputPath = null)
        {
            var root = Path.Combine(
                AppContext.BaseDirectory,
                "converter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var executable = Path.Combine(root, "synthetic converter.exe");
            File.Copy(compiledFakeConverter, executable, overwrite: false);
            var input = Path.Combine(root, relativeInputPath ?? "neutral.cdb");
            Directory.CreateDirectory(Path.GetDirectoryName(input)!);
            await File.WriteAllTextAsync(input, "not-empty").ConfigureAwait(false);
            var fixture = new ConverterFixture(root, executable, input);
            await fixture.ConfigureAsync(scenario).ConfigureAwait(false);
            return fixture;
        }

        public Task ConfigureAsync(params string[] lines) =>
            File.WriteAllLinesAsync(Path.Combine(Root, "scenario.txt"), lines);

        public void Dispose()
        {
            TryDeleteDirectory(Root);
        }
    }

    private sealed class TimeoutCapturingConverter : ICdbConverter
    {
        public TimeSpan? ExportTimeout { get; private set; }

        public async Task<ConversionResult> ExportToSqliteAsync(
            string workingCdbPath,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ExportTimeout = timeout;
            var output = Path.ChangeExtension(workingCdbPath, ".sqlite");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = output,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE neutral(ID INTEGER PRIMARY KEY, value TEXT)";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new ConversionResult(output, new ConverterDiagnostics(0, string.Empty, string.Empty, TimeSpan.Zero));
        }

        public Task<ConversionResult> ImportToCdbAsync(
            string workingSqlitePath,
            string temporaryCdbDestination,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
