using EyRiskScreening.Application.Suppliers;
using EyRiskScreening.Domain.Suppliers;
using EyRiskScreening.UnitTests.Fakes;
using EyRiskScreening.UnitTests.Infrastructure;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class SupplierServiceTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 25, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidCreateUsesServerTimeAndPersistsTrimmedValues()
    {
        var store = new FakeSupplierStore();
        var service = CreateService(store, out _);

        var result = await service.CreateAsync(
            SupplierValidatorTests.ValidInput() with
            {
                LegalName = "  PARS TABLEAU COMPANY  ",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(SupplierOperationOutcome.Success, result.Outcome);
        Assert.Equal(1, store.CreateCalls);
        Assert.Equal("PARS TABLEAU COMPANY", result.Supplier?.LegalName);
        Assert.Equal(InitialTime, result.Supplier?.LastEditedAtUtc);
        Assert.NotEqual(Guid.Empty, result.Supplier?.Id);
    }

    [Fact]
    public async Task InvalidCreateDoesNotReachStore()
    {
        var store = new FakeSupplierStore();
        var service = CreateService(store, out _);

        var result = await service.CreateAsync(
            SupplierValidatorTests.ValidInput() with { TaxId = "invalid" },
            TestContext.Current.CancellationToken);

        Assert.Equal(SupplierOperationOutcome.Invalid, result.Outcome);
        Assert.Equal(0, store.CreateCalls);
    }

    [Fact]
    public async Task DuplicateTaxIdReturnsConflict()
    {
        var store = new FakeSupplierStore();
        store.Suppliers.Add(Supplier());
        var service = CreateService(store, out _);

        var result = await service.CreateAsync(
            SupplierValidatorTests.ValidInput(),
            TestContext.Current.CancellationToken);

        Assert.Equal(SupplierOperationOutcome.TaxIdConflict, result.Outcome);
        Assert.Equal(0, store.CreateCalls);
    }

    [Fact]
    public async Task RealUpdateUsesNewServerTimeAndPreservesId()
    {
        var store = new FakeSupplierStore();
        var supplier = Supplier();
        store.Suppliers.Add(supplier);
        var service = CreateService(store, out var timeProvider);
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        var result = await service.UpdateAsync(
            supplier.Id,
            SupplierValidatorTests.ValidInput() with
            {
                CommercialName = "Updated name",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(SupplierOperationOutcome.Success, result.Outcome);
        Assert.Equal(supplier.Id, result.Supplier?.Id);
        Assert.Equal(InitialTime.AddMinutes(5), result.Supplier?.LastEditedAtUtc);
        Assert.Equal(1, store.UpdateCalls);
    }

    [Fact]
    public async Task IdenticalUpdateDoesNotChangeTimestampOrWrite()
    {
        var store = new FakeSupplierStore();
        var supplier = Supplier();
        store.Suppliers.Add(supplier);
        var service = CreateService(store, out var timeProvider);
        timeProvider.Advance(TimeSpan.FromHours(1));

        var result = await service.UpdateAsync(
            supplier.Id,
            SupplierValidatorTests.ValidInput(),
            TestContext.Current.CancellationToken);

        Assert.Equal(InitialTime, result.Supplier?.LastEditedAtUtc);
        Assert.Equal(0, store.UpdateCalls);
    }

    [Fact]
    public async Task UpdateAllowsCurrentTaxIdButRejectsAnotherSupplier()
    {
        var store = new FakeSupplierStore();
        var supplier = Supplier();
        store.Suppliers.Add(supplier);
        store.Suppliers.Add(Supplier() with
        {
            Id = Guid.NewGuid(),
            TaxId = "20999999999",
        });
        var service = CreateService(store, out _);

        var same = await service.UpdateAsync(
            supplier.Id,
            SupplierValidatorTests.ValidInput() with
            {
                CommercialName = "Changed",
            },
            TestContext.Current.CancellationToken);
        var conflict = await service.UpdateAsync(
            supplier.Id,
            SupplierValidatorTests.ValidInput() with
            {
                TaxId = "20999999999",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(SupplierOperationOutcome.Success, same.Outcome);
        Assert.Equal(SupplierOperationOutcome.TaxIdConflict, conflict.Outcome);
    }

    [Fact]
    public async Task MissingGetUpdateAndDeleteReturnNotFound()
    {
        var service = CreateService(new FakeSupplierStore(), out _);
        var id = Guid.NewGuid();

        var get = await service.GetAsync(
            id,
            TestContext.Current.CancellationToken);
        var update = await service.UpdateAsync(
            id,
            SupplierValidatorTests.ValidInput(),
            TestContext.Current.CancellationToken);
        var delete = await service.DeleteAsync(
            id,
            TestContext.Current.CancellationToken);

        Assert.Equal(SupplierOperationOutcome.NotFound, get.Outcome);
        Assert.Equal(SupplierOperationOutcome.NotFound, update.Outcome);
        Assert.Equal(SupplierOperationOutcome.NotFound, delete.Outcome);
    }

    private static SupplierService CreateService(
        FakeSupplierStore store,
        out ManualTimeProvider timeProvider)
    {
        timeProvider = new ManualTimeProvider(InitialTime);
        return new SupplierService(
            store,
            new FakeSupplierFailureReporter(),
            timeProvider);
    }

    private static Supplier Supplier() =>
        new(
            Guid.NewGuid(),
            "PARS TABLEAU COMPANY",
            "Pars Tableau",
            "20123456789",
            "+51 999 999 999",
            "contact@example.com",
            "https://example.com",
            "Av. Ejemplo 123",
            "Peru",
            1250000.50m,
            InitialTime);
}
