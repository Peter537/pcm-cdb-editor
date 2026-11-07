using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

public enum SaveDestinationConflictReason
{
    Changed,
    BaselineUnknown
}

public sealed class SaveDestinationConflictException : IOException
{
    public SaveDestinationConflictException(
        SaveDestinationConflictReason reason,
        SaveDestinationSnapshot observedDestination)
        : base(reason == SaveDestinationConflictReason.BaselineUnknown
            ? "The saved destination baseline is unavailable."
            : "The save destination changed since it was opened or last confirmed.")
    {
        Reason = reason;
        ObservedDestination = observedDestination;
    }

    public SaveDestinationConflictReason Reason { get; }

    public SaveDestinationSnapshot ObservedDestination { get; }
}
