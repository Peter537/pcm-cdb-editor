using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

public sealed class RiderGameDisplayNameState
{
    public string Value { get; private set; } = string.Empty;

    public bool IsOverridden { get; private set; }

    public bool UpdateNames(string? firstName, string? lastName)
    {
        if (IsOverridden)
        {
            return false;
        }

        string generated = RiderGameDisplayName.Generate(firstName, lastName);
        if (generated.Equals(Value, StringComparison.Ordinal))
        {
            return false;
        }

        Value = generated;
        return true;
    }

    public void Override(string? value)
    {
        Value = value ?? string.Empty;
        IsOverridden = true;
    }

    public void Reset(string? firstName, string? lastName)
    {
        IsOverridden = false;
        Value = RiderGameDisplayName.Generate(firstName, lastName);
    }
}

public static class RiderCreationCommandAvailability
{
    public static bool CanCreate(
        bool hasCurrentPreview,
        int missingLimitCount,
        bool missingLimitsAcknowledged,
        bool isBusy,
        bool hasExclusiveOperation) =>
        hasCurrentPreview
        && missingLimitCount >= 0
        && (missingLimitCount == 0 || missingLimitsAcknowledged)
        && !isBusy
        && !hasExclusiveOperation;
}
