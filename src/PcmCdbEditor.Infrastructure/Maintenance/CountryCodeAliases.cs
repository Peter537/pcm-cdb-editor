namespace PcmCdbEditor.Infrastructure.Maintenance;

internal static class CountryCodeAliases
{
    private static readonly Dictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["den"] = "DNK",
            ["ned"] = "NLD",
            ["ger"] = "DEU",
            ["SWD"] = "SWE",
            ["CRO"] = "HRV",
            ["lat"] = "LVA",
            ["swi"] = "CHE",
            ["MAS"] = "MYS",
            ["SER"] = "SRB",
            ["BUL"] = "BGR",
            ["GRE"] = "GRC",
            ["CRC"] = "CRI",
            ["ZIM"] = "ZWE",
            ["BER"] = "BMU",
            ["MOL"] = "MDA",
            ["ROM"] = "ROU",
            ["KOS"] = "XK",
            ["SLO"] = "SVN",
            ["POR"] = "PRT",
            ["CHI"] = "CHN",
            ["KUW"] = "KWT",
            ["OMA"] = "OMN",
            ["SAR"] = "ZAF",
            ["UAE"] = "ARE",
            ["URU"] = "URY"
        };

    public static string Canonicalize(string? sourceCode)
    {
        var normalized = string.IsNullOrWhiteSpace(sourceCode) ? "UNK" : sourceCode.Trim();
        return Aliases.TryGetValue(normalized, out var canonical)
            ? canonical
            : normalized.ToUpperInvariant();
    }

    public static IReadOnlyDictionary<string, string> All => Aliases;
}
