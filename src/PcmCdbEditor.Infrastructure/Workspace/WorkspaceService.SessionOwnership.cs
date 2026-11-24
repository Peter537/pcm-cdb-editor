using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Infrastructure.Workspace;

public sealed partial class WorkspaceService
{
    private readonly object _sessionOwnershipGate = new();
    private readonly Dictionary<Guid, SessionOwnership> _ownedSessions = [];
    private bool _disposed;

    private sealed class SessionOwnership(FileStream lease)
    {
        public FileStream Lease { get; } = lease;
        public CommittedSaveReceipt? UnsynchronizedSave { get; set; }
    }

    private sealed record CommittedSaveReceipt(EditorSessionState Saving, EditorSessionState Committed);

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
            _ownedSessions.Add(sessionId, new SessionOwnership(ownership));
        }
    }

    private void RememberUnsynchronizedSave(EditorSessionState saving, EditorSessionState committed)
    {
        lock (_sessionOwnershipGate)
        {
            if (_ownedSessions.TryGetValue(committed.SessionId, out SessionOwnership? ownership))
            {
                ownership.UnsynchronizedSave = new CommittedSaveReceipt(saving, committed);
            }
        }
    }

    private CommittedSaveReceipt? GetUnsynchronizedSave(Guid sessionId)
    {
        lock (_sessionOwnershipGate)
        {
            return _ownedSessions.GetValueOrDefault(sessionId)?.UnsynchronizedSave;
        }
    }

    private void ClearUnsynchronizedSave(Guid sessionId)
    {
        lock (_sessionOwnershipGate)
        {
            if (_ownedSessions.TryGetValue(sessionId, out SessionOwnership? ownership))
            {
                ownership.UnsynchronizedSave = null;
            }
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
            if (_ownedSessions.Remove(sessionId, out SessionOwnership? ownership))
            {
                ownership.Lease.Dispose();
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
            foreach (SessionOwnership ownership in _ownedSessions.Values)
            {
                ownership.Lease.Dispose();
            }
            _ownedSessions.Clear();
        }

        GC.SuppressFinalize(this);
    }
}
