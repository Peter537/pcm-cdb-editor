using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class InlineEditCommitStagerTests
{
    [TestMethod]
    public void SuccessfulPostCommitCompletionPublishesExactlyOnce()
    {
        var stager = new InlineEditCommitStager();
        var rowToken = new object();
        EditOperation operation = CreateOperation();

        stager.Stage(operation, 7, rowToken, "gene_sz_lastname");

        Assert.AreSame(operation, stager.Complete(true, 7, rowToken, "GENE_SZ_LASTNAME"));
        Assert.IsNull(stager.Complete(true, 7, rowToken, "gene_sz_lastname"));
    }

    [TestMethod]
    public void CancellationAndExplicitClearDiscardThePendingEdit()
    {
        var stager = new InlineEditCommitStager();
        var rowToken = new object();

        stager.Stage(CreateOperation(), 2, rowToken, "value_i_rank");
        Assert.IsNull(stager.Complete(false, 2, rowToken, "value_i_rank"));

        stager.Stage(CreateOperation(), 2, rowToken, "value_i_rank");
        stager.Clear();
        Assert.IsNull(stager.Complete(true, 2, rowToken, "value_i_rank"));
    }

    [TestMethod]
    public void RebindOrDifferentCellInvalidatesThePendingEdit()
    {
        var stager = new InlineEditCommitStager();
        var rowToken = new object();

        stager.Stage(CreateOperation(), 3, rowToken, "value_f_stat");
        Assert.IsNull(stager.Complete(true, 4, rowToken, "value_f_stat"));

        stager.Stage(CreateOperation(), 4, rowToken, "value_f_stat");
        Assert.IsNull(stager.Complete(true, 4, new object(), "value_f_stat"));

        stager.Stage(CreateOperation(), 4, rowToken, "value_f_stat");
        Assert.IsNull(stager.Complete(true, 4, rowToken, "other_column"));
    }

    private static CellUpdateOperation CreateOperation() => new(
        Guid.NewGuid(),
        "DYN_cyclist",
        DateTimeOffset.UtcNow,
        RowIdentity.FromRowId(1),
        "gene_sz_lastname",
        SqliteValue.Text("before"),
        SqliteValue.Text("after"),
        new RowRevision("revision"));
}
