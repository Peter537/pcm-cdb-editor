using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class BoundedVirtualWindowTests
{
    private static readonly int[] ExpectedNewestItems = [2, 3, 4, 5, 6, 7, 8, 9];
    private static readonly string[] ExpectedReplacementItems = ["new"];
    private static readonly string[] ExpectedEarlierReplacementItems = ["new", "later"];
    private static readonly int[] ExpectedResetItems = [9];

    [TestMethod]
    public void AddKeepsOnlyTheNewestFourChunks()
    {
        var window = new BoundedVirtualWindow<int>();
        for (var chunk = 0; chunk < 5; chunk++)
        {
            window.Add(new VirtualChunk<int>(chunk * 2, [chunk * 2, (chunk * 2) + 1]));
        }

        Assert.AreEqual(BoundedVirtualWindow<int>.MaximumChunks, window.Chunks.Count);
        Assert.AreEqual(2L, window.FirstOffset);
        Assert.AreEqual(10L, window.NextOffset);
        CollectionAssert.AreEqual(ExpectedNewestItems, window.Items.ToArray());
    }

    [TestMethod]
    public void AddReplacesSameOffsetWithoutGrowingWindow()
    {
        var window = new BoundedVirtualWindow<string>();
        window.Add(new VirtualChunk<string>(0, ["old"]));
        window.Add(new VirtualChunk<string>(0, ["new"]));

        Assert.HasCount(1, window.Chunks);
        CollectionAssert.AreEqual(ExpectedReplacementItems, window.Items.ToArray());
    }

    [TestMethod]
    public void AddReplacesEarlierChunkWithoutDiscardingLaterChunks()
    {
        var window = new BoundedVirtualWindow<string>();
        window.Add(new VirtualChunk<string>(0, ["old"]));
        window.Add(new VirtualChunk<string>(1, ["later"]));
        window.Add(new VirtualChunk<string>(0, ["new"]));

        CollectionAssert.AreEqual(ExpectedEarlierReplacementItems, window.Items.ToArray());
    }

    [TestMethod]
    public void AddRejectsBackwardOffsetsAndResetStartsNewWindow()
    {
        var window = new BoundedVirtualWindow<int>();
        window.Add(new VirtualChunk<int>(20, [1]));
        Assert.ThrowsExactly<InvalidOperationException>(() => window.Add(new VirtualChunk<int>(10, [2])));

        window.Reset(new VirtualChunk<int>(100, [9]));
        Assert.AreEqual(100L, window.FirstOffset);
        CollectionAssert.AreEqual(ExpectedResetItems, window.Items.ToArray());
    }
}
