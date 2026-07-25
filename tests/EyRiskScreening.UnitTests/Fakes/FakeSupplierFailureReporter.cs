using EyRiskScreening.Application.Suppliers;

namespace EyRiskScreening.UnitTests.Fakes;

internal sealed class FakeSupplierFailureReporter : ISupplierFailureReporter
{
    public int Calls { get; private set; }

    public void Report(
        string operation,
        Guid? supplierId,
        Exception exception) =>
        Calls++;
}
