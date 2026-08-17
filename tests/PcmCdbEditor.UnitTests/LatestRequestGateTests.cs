using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class LatestRequestGateTests
{
    [TestMethod]
    public void NewLeaseSupersedesEveryOlderLease()
    {
        var gate = new LatestRequestGate();

        LatestRequestGate.RequestLease first = gate.Begin();
        LatestRequestGate.RequestLease second = gate.Begin();

        Assert.IsFalse(first.IsCurrent);
        Assert.IsTrue(second.IsCurrent);
        Assert.IsGreaterThan(0L, first.Generation);
        Assert.IsGreaterThan(first.Generation, second.Generation);
    }

    [TestMethod]
    public void InvalidateSuppressesTheCurrentCompletion()
    {
        var gate = new LatestRequestGate();
        LatestRequestGate.RequestLease lease = gate.Begin();

        gate.Invalidate();

        Assert.IsFalse(lease.IsCurrent);
        Assert.Throws<OperationCanceledException>(() =>
            lease.ThrowIfSuperseded(CancellationToken.None));
    }

    [TestMethod]
    public void ExplicitCancellationWinsEvenForTheCurrentLease()
    {
        var gate = new LatestRequestGate();
        LatestRequestGate.RequestLease lease = gate.Begin();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            lease.ThrowIfSuperseded(cancellation.Token));
        Assert.IsTrue(lease.IsCurrent);
    }
}
