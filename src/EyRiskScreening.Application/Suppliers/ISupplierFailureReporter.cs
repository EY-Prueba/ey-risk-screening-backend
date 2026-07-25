namespace EyRiskScreening.Application.Suppliers;

public interface ISupplierFailureReporter
{
    void Report(string operation, Guid? supplierId, Exception exception);
}
