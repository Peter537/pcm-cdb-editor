using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class MaintenanceModelTests
{
    [TestMethod]
    public void RiderRecoveryDefaultMatchesApprovedPreset()
    {
        var preset = RiderRecoveryValues.Default;

        Assert.AreEqual(99d, preset.Fit);
        Assert.AreEqual(0d, preset.Injury);
        Assert.AreEqual(0L, preset.InjuryDays);
        Assert.AreEqual(0d, preset.PhysicalFatigue);
        Assert.AreEqual(100d, preset.Freshness);
        Assert.AreEqual(99d, preset.Preparation);
    }

    [TestMethod]
    public void RiderPreviewNormalizesIdsButRetainsTypedChangeSnapshots()
    {
        var oldValues = new RiderRecoveryValues(1, 2, 3, 4, 5, 6);
        var change = new RiderRecoveryChange(7, oldValues, RiderRecoveryValues.Default);
        var preview = new RiderRecoveryPreview("snapshot", [7, 7, 5], [change]);

        CollectionAssert.AreEqual(new long[] { 5, 7 }, preview.CyclistIds.ToArray());
        Assert.AreEqual(oldValues, preview.Changes[0].OldValues);
        Assert.AreEqual("snapshot", preview.SnapshotToken);
    }

    [TestMethod]
    public void CapabilityFreezesDetailedMissingSchemaReasons()
    {
        var missingTables = new List<string> { "table_a" };
        var capability = new MaintenanceCapability(
            MaintenanceToolKind.RiderRecovery,
            false,
            ["Required schema is missing."],
            missingTables,
            ["table_a.column_b"]);
        missingTables.Add("later");

        Assert.IsFalse(capability.IsEnabled);
        Assert.HasCount(1, capability.MissingTables);
        Assert.AreEqual("table_a.column_b", capability.MissingColumns[0]);
    }
}
