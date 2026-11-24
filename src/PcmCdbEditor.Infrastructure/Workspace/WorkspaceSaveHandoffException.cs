using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Infrastructure.Workspace;

internal sealed class WorkspaceSaveHandoffException(
    EditorSessionState session, string destination, string stage, string? backup, IOException cause)
    : IOException("The destination could not be replaced completely.", cause)
{
    public EditorSessionState Session { get; } = session;
    public string Destination { get; } = destination;
    public string Stage { get; } = stage;
    public string? Backup { get; } = backup;
}
