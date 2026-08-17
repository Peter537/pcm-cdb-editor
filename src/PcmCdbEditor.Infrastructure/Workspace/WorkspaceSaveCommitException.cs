using PcmCdbEditor.Application;

namespace PcmCdbEditor.Infrastructure.Workspace;

internal sealed class WorkspaceSaveCommitException : IOException
{
    public WorkspaceSaveCommitException(
        WorkspaceSaveResult committedSave,
        Exception innerException)
        : base(
            "The destination was saved, but the editor session metadata could not be synchronized.",
            innerException)
    {
        CommittedSave = committedSave;
    }

    public WorkspaceSaveResult CommittedSave { get; }
}
