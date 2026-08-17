using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class ArchitectureBoundaryTests
{
    [TestMethod]
    public void DomainDoesNotReferenceApplicationOrInfrastructure()
    {
        var references = typeof(TypedRow).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        CollectionAssert.DoesNotContain(references, "PcmCdbEditor.Application");
        CollectionAssert.DoesNotContain(references, "PcmCdbEditor.Infrastructure");
        CollectionAssert.DoesNotContain(references, "Microsoft.Data.Sqlite");
    }

    [TestMethod]
    public void ApplicationContractsDoNotExposeUiOrSqlitePackages()
    {
        var references = typeof(ICdbConverter).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        CollectionAssert.DoesNotContain(references, "Microsoft.Data.Sqlite");
        CollectionAssert.DoesNotContain(references, "Microsoft.UI.Xaml");
        CollectionAssert.DoesNotContain(references, "WinUI.TableView");
    }
}
