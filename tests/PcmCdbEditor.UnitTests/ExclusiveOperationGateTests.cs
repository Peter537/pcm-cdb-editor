using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class ExclusiveOperationGateTests
{
    [TestMethod]
    public void CompetingEntryIsRefusedWithoutCancellingTheActiveOperation()
    {
        using var gate = new ExclusiveOperationGate();
        Assert.IsTrue(gate.TryEnter(CancellationToken.None, out var first));
        using ExclusiveOperationGate.ExclusiveOperationLease active = first!;

        Assert.IsFalse(gate.TryEnter(CancellationToken.None, out var competing));
        Assert.IsNull(competing);
        Assert.IsFalse(active.Token.IsCancellationRequested);
        Assert.IsTrue(gate.IsActive);

        active.Cancel();
        Assert.IsTrue(active.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void DisposingTheLeaseAllowsExactlyOneLaterOperation()
    {
        using var gate = new ExclusiveOperationGate();
        Assert.IsTrue(gate.TryEnter(CancellationToken.None, out var first));
        first!.Dispose();
        first.Dispose();

        Assert.IsFalse(gate.IsActive);
        Assert.IsTrue(gate.TryEnter(CancellationToken.None, out var second));
        Assert.IsFalse(gate.TryEnter(CancellationToken.None, out _));
        second!.Dispose();
        Assert.IsFalse(gate.IsActive);
    }

    [TestMethod]
    public void LifetimeCancellationReachesOnlyTheCurrentLease()
    {
        using var gate = new ExclusiveOperationGate();
        using var lifetime = new CancellationTokenSource();
        Assert.IsTrue(gate.TryEnter(lifetime.Token, out var lease));
        using ExclusiveOperationGate.ExclusiveOperationLease active = lease!;

        lifetime.Cancel();

        Assert.IsTrue(active.Token.IsCancellationRequested);
        Assert.IsTrue(gate.IsActive);
    }
}
