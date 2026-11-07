namespace PcmCdbEditor.Infrastructure.Workspace;

public sealed partial class WorkspaceService
{
    private readonly object _sessionOwnershipGate = new();
    private readonly Dictionary<Guid, FileStream> _ownedSessions = [];
    private bool _disposed;

    private FileStream AcquireSessionOwnership(Guid sessionId)
    {
        lock (_sessionOwnershipGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        // Keep the handle outside the session so a discard can remove the
        // working files while the same exclusive lease still protects them.
        string leaseDirectory = Path.Combine(_sessionsRoot, ".leases");
        Directory.CreateDirectory(leaseDirectory);
        var leasePath = Path.Combine(leaseDirectory, $"{sessionId:N}.lock");
        try
        {
            return new FileStream(
                leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, options: FileOptions.DeleteOnClose);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        {
            throw new InvalidOperationException(
                "The working session is already in use. Refresh Recovery after it closes.", exception);
        }
    }

    private void RetainSessionOwnership(Guid sessionId, FileStream ownership)
    {
        lock (_sessionOwnershipGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ownedSessions.Add(sessionId, ownership);
        }
    }

    private void RequireSessionOwnership(Guid sessionId)
    {
        lock (_sessionOwnershipGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_ownedSessions.ContainsKey(sessionId))
            {
                throw new InvalidOperationException("This editor does not own the working session.");
            }
        }
    }

    public void ReleaseSession(Guid sessionId)
    {
        lock (_sessionOwnershipGate)
        {
            if (_ownedSessions.Remove(sessionId, out FileStream? ownership))
            {
                ownership.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_sessionOwnershipGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (FileStream ownership in _ownedSessions.Values)
            {
                ownership.Dispose();
            }
            _ownedSessions.Clear();
        }

        GC.SuppressFinalize(this);
    }
}
