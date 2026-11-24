using Microsoft.UI.Xaml;
using PcmCdbEditor.Infrastructure.Conversion;
using PcmCdbEditor.Infrastructure.Maintenance;
using PcmCdbEditor.Infrastructure.FileAssociations;
using PcmCdbEditor.Infrastructure.Settings;
using PcmCdbEditor.Infrastructure.Sqlite;
using PcmCdbEditor.Infrastructure.Workspace;

namespace PcmCdbEditor.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string? launchPath = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(static argument =>
                Path.GetExtension(argument).Equals(".cdb", StringComparison.OrdinalIgnoreCase));
        string applicationDataRoot = ResolveApplicationDataRoot();
        var converter = new CdbConverter(ResolveExporterPath());
        var workspaceService = new WorkspaceService(
            converter,
            Path.Combine(applicationDataRoot, "Sessions"),
            Path.Combine(applicationDataRoot, "Backups"));
        var tableCatalog = new SqliteTableCatalog();
        var tableDataStore = new SqliteTableDataStore();
        var riderRecoveryService = new RiderRecoveryService();
        var riderCreationService = new RiderCreationService();
        var januaryFirstRepairService = new JanuaryFirstRepairService();
        var countryQuotaMaintenanceService = new CountryQuotaMaintenanceService();
        var settingsStore = new JsonSettingsStore(Path.Combine(applicationDataRoot, "settings.json"));
        var fileAssociationService = new WindowsFileAssociationService();

        _window = new MainWindow(
            launchPath,
            workspaceService,
            tableCatalog,
            tableDataStore,
            new PcmCdbEditor.Infrastructure.History.SqliteEditOperationReplayer(),
            new SqliteRelationshipLookup(),
            riderRecoveryService,
            riderCreationService,
            januaryFirstRepairService,
            countryQuotaMaintenanceService,
            settingsStore,
            fileAssociationService);
        _window.Closed += (_, _) => workspaceService.Dispose();
        _window.Activate();
    }

    private static string ResolveApplicationDataRoot()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("PCM_CDB_EDITOR_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PcmCdbEditor");
        }

        if (!Path.IsPathFullyQualified(configuredRoot))
        {
            throw new ArgumentException("PCM_CDB_EDITOR_DATA_ROOT must be an absolute directory path.");
        }

        return Path.GetFullPath(configuredRoot);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        // Diagnostics intentionally exclude exception text because provider messages can
        // contain database values. The window presents sanitized operation errors instead.
        System.Diagnostics.Debug.WriteLine("An unhandled UI exception occurred.");
    }

    private static string ResolveExporterPath()
    {
        const string exporterDirectory = "SQLiteExporter";
        const string exporterFileName = "SQLiteExporter.exe";
        string publishedPath = Path.Combine(
            AppContext.BaseDirectory,
            "third_party",
            exporterDirectory,
            exporterFileName);
        if (File.Exists(publishedPath))
        {
            return publishedPath;
        }

        // Development fallback: inspect only a bounded set of ancestors for the repository's
        // declared third-party tool. Never search user folders or legacy/quarantine trees.
        DirectoryInfo? ancestor = new(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && ancestor is not null; depth++, ancestor = ancestor.Parent)
        {
            string candidate = Path.Combine(
                ancestor.FullName,
                "third_party",
                exporterDirectory,
                exporterFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Keep the published location as the configured path so the converter returns its
        // structured, sanitized missing-executable failure when packaging is incomplete.
        return publishedPath;
    }
}
