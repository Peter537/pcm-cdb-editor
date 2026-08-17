using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Infrastructure.Conversion;
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
    public async Task TimeoutTerminatesTheRunningProcessAndPreservesBoundedOutput()
    {
        using var fixture = await ConverterFixture.CreateAsync(s_fakeConverterPath, "sleep")
            .ConfigureAwait(false);
        var converter = new CdbConverter(fixture.ExecutablePath);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsExactlyAsync<CdbConversionException>(() =>
            converter.ExportToSqliteAsync(
                fixture.InputPath,
                TimeSpan.FromMilliseconds(400),
                CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual(ConverterFailureCategory.TimedOut, exception.Failure.Category);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "The timeout path was not bounded.");
        var diagnostics = exception.Failure.Diagnostics
            ?? throw new AssertFailedException("Timeout diagnostics were missing.");
        StringAssert.Contains(diagnostics.StandardOutput, "ready", StringComparison.Ordinal);
        var processId = int.Parse(
            await File.ReadAllTextAsync(Path.Combine(fixture.Root, "parent.pid")).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        await AssertProcessExitedAsync(processId).ConfigureAwait(false);
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
    public async Task ArgumentListPreservesSpacesAndShellMetacharactersLiterally()
    {
        const string hostileButValidName = "literal space & (group) ^ ! % ; [value]";
        using var exportFixture = await ConverterFixture.CreateAsync(
                s_fakeConverterPath,
                "validate",
                Path.Combine(hostileButValidName, "source & echo not-a-command.cdb"))
            .ConfigureAwait(false);
        await exportFixture.ConfigureAsync(
                "validate",
                "-export",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(exportFixture.InputPath)))
            .ConfigureAwait(false);
        var exportConverter = new CdbConverter(exportFixture.ExecutablePath);

        var export = await exportConverter.ExportToSqliteAsync(
                exportFixture.InputPath,
                TimeSpan.FromMinutes(1),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(Path.ChangeExtension(exportFixture.InputPath, ".sqlite"), export.OutputPath);
        Assert.AreEqual(
            "synthetic converter output",
            await File.ReadAllTextAsync(export.OutputPath).ConfigureAwait(false));

        using var importFixture = await ConverterFixture.CreateAsync(
                s_fakeConverterPath,
                "validate",
                Path.Combine(hostileButValidName, "working & literal.sqlite"))
            .ConfigureAwait(false);
        var destination = Path.Combine(importFixture.Root, hostileButValidName, "save & literal (copy).cdb");
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

    private static async Task<int> ReadProcessIdAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
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

        throw new AssertFailedException($"The fake converter did not create {path} within five seconds.");
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
                Path.GetTempPath(),
                "PcmCdbEditorTests",
                "ConverterRuns",
                Guid.NewGuid().ToString("N"));
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
