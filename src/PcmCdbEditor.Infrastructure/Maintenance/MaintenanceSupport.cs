using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.Infrastructure.Maintenance;

internal static class MaintenanceSupport
{
    public static Task<MaintenanceCapability> CheckAsync(
        string sqlitePath,
        MaintenanceToolKind tool,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> requirements,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => CheckCoreAsync(sqlitePath, tool, requirements, cancellationToken),
            cancellationToken);

    private static async Task<MaintenanceCapability> CheckCoreAsync(
        string sqlitePath,
        MaintenanceToolKind tool,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> requirements,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        var tables = await SqliteSupport.ReadTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        var missingTables = requirements.Keys.Where(table => !tables.Contains(table)).Order().ToArray();
        var missingColumns = new List<string>();
        foreach (var requirement in requirements.Where(pair => tables.Contains(pair.Key)))
        {
            var columns = await SqliteSupport.ReadColumnNamesAsync(connection, requirement.Key, cancellationToken)
                .ConfigureAwait(false);
            missingColumns.AddRange(requirement.Value
                .Where(column => !columns.Contains(column))
                .Select(column => $"{requirement.Key}.{column}"));
        }

        var enabled = missingTables.Length == 0 && missingColumns.Count == 0;
        var reasons = enabled
            ? Array.Empty<string>()
            : new[] { "The database does not expose the complete schema required by this tool." };
        return new MaintenanceCapability(tool, enabled, reasons, missingTables, missingColumns.Order());
    }

    public static void RequireEnabled(MaintenanceCapability capability)
    {
        if (!capability.IsEnabled)
        {
            throw new InvalidOperationException(string.Join(
                " ",
                capability.Reasons.Concat(capability.MissingTables).Concat(capability.MissingColumns)));
        }
    }

    public static string ComputeToken(IEnumerable<string> values)
    {
        var text = string.Join('\u001f', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string CanonicalNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string CanonicalNumber(long value) => value.ToString(CultureInfo.InvariantCulture);
}
