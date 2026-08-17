using System.Collections.ObjectModel;

namespace PcmCdbEditor.Domain;

internal static class ModelCollections
{
    public static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T>? values)
    {
        return Array.AsReadOnly((values ?? []).ToArray());
    }

    public static ReadOnlyDictionary<string, TValue> FreezeDictionary<TValue>(
        IEnumerable<KeyValuePair<string, TValue>>? values)
    {
        var copy = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("Column names must be non-empty SQLite identifiers.", nameof(values));
            }

            if (!copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException($"Duplicate column '{pair.Key}'.", nameof(values));
            }
        }

        return new ReadOnlyDictionary<string, TValue>(copy);
    }
}
