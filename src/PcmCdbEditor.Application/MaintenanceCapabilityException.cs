using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

public sealed class MaintenanceCapabilityException(MaintenanceCapability capability)
    : InvalidOperationException(Describe(capability))
{
    public MaintenanceCapability Capability { get; } = capability;

    private static string Describe(MaintenanceCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var details = new List<string>(capability.Reasons);
        if (capability.MissingTables.Count != 0)
        {
            details.Add($"Missing tables: {string.Join(", ", capability.MissingTables)}.");
        }

        if (capability.MissingColumns.Count != 0)
        {
            details.Add($"Missing columns: {string.Join(", ", capability.MissingColumns)}.");
        }

        return details.Count == 0 ? "The open database does not support this maintenance tool." : string.Join(" ", details);
    }
}
