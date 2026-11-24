using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.Infrastructure.Maintenance;

internal static class MaintenanceSupport
{
    public static async Task<MaintenanceCapability> CheckAsync(
        string sqlitePath,
        Func<DatabaseSchemaCatalog, MaintenanceCapability> check,
        CancellationToken cancellationToken)
    {
        var catalog = await new SqliteTableCatalog().DiscoverAsync(sqlitePath, cancellationToken)
            .ConfigureAwait(false);
        return check(catalog);
    }

    public static async Task<DatabaseSchemaCatalog> RequireCatalogAsync(
        string sqlitePath,
        Func<DatabaseSchemaCatalog, MaintenanceCapability> check,
        CancellationToken cancellationToken)
    {
        var catalog = await new SqliteTableCatalog().DiscoverAsync(sqlitePath, cancellationToken)
            .ConfigureAwait(false);
        RequireEnabled(check(catalog));
        return catalog;
    }

    public static MaintenanceCapability Check(
        DatabaseSchemaCatalog catalog,
        MaintenanceToolKind tool,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> requirements,
        IReadOnlyCollection<string> mutationTargets,
        Func<TableSchema, string?>? validateTarget = null)
    {
        var missingTables = new List<string>();
        var missingColumns = new List<string>();
        foreach (var (tableName, requiredColumns) in requirements)
        {
            if (!catalog.TryGetTable(tableName, out TableSchema? table))
            {
                missingTables.Add(tableName);
                continue;
            }

            var columns = table.Columns.Select(static column => column.Name)
                .ToHashSet(SqliteIdentifierComparer.Instance);
            missingColumns.AddRange(requiredColumns
                .Where(column => !columns.Contains(column))
                .Select(column => $"{tableName}.{column}"));
        }

        var reasons = new List<string>();
        if (missingTables.Count != 0 || missingColumns.Count != 0)
        {
            reasons.Add("The database does not expose the complete schema required by this tool.");
        }

        foreach (string target in mutationTargets)
        {
            if (!catalog.TryGetTable(target, out TableSchema? table))
            {
                continue;
            }

            if (table.ObjectKind != TableObjectKind.Table || table.EditCapability != TableEditCapability.Editable)
            {
                reasons.Add(
                    $"The mutation target '{target}' must be an ordinary editable table with a stable row identity.");
            }
            else if (validateTarget?.Invoke(table) is { } reason)
            {
                reasons.Add(reason);
            }
        }

        return new MaintenanceCapability(tool, reasons.Count == 0, reasons, missingTables.Order(), missingColumns.Order());
    }

    public static void RequireEnabled(MaintenanceCapability capability)
    {
        if (!capability.IsEnabled)
        {
            throw new MaintenanceCapabilityException(capability);
        }
    }

    public static string ComputeToken(IEnumerable<string> values)
    {
        var text = string.Join('\u001f', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string CanonicalNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string CanonicalNumber(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string CanonicalValue(SqliteValue value) => value.Kind switch
    {
        SqliteValueKind.Null => "N",
        SqliteValueKind.Integer => $"I:{CanonicalNumber(value.IntegerValue)}",
        SqliteValueKind.Real => $"R:{BitConverter.DoubleToInt64Bits(value.RealValue):X16}",
        SqliteValueKind.Text => $"T:{Convert.ToBase64String(Encoding.UTF8.GetBytes(value.TextValue ?? string.Empty))}",
        SqliteValueKind.Blob => $"B:{value.BlobBase64}",
        _ => throw new InvalidOperationException($"Unsupported SQLite value kind '{value.Kind}'.")
    };
}
