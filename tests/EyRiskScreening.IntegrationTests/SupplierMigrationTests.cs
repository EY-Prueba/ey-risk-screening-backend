using EyRiskScreening.Application.Suppliers;
using EyRiskScreening.Domain.Suppliers;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
public sealed class SupplierMigrationTests(SqlServerFixture sqlServer)
    : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await sqlServer.CreateMigratedDatabaseAsync(
            "EyRiskScreening_SupplierMigrationTests",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task MigrationCreatesOnlyExpectedSupplierSchemaObjects()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var columns = await ReadStringsAsync(
            connection,
            """
            SELECT CONCAT(COLUMN_NAME, '|', DATA_TYPE, '|',
                COALESCE(CONVERT(varchar(20), CHARACTER_MAXIMUM_LENGTH), ''),
                '|', IS_NULLABLE)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'Suppliers'
            ORDER BY ORDINAL_POSITION
            """);
        Assert.Equal(11, columns.Length);
        Assert.Contains("TaxId|varchar|11|NO", columns);
        Assert.Contains("AnnualBillingUsd|decimal||NO", columns);
        Assert.Contains("LastEditedAtUtc|datetimeoffset||NO", columns);

        var indexes = await ReadStringsAsync(
            connection,
            """
            SELECT CONCAT(name, '|', is_unique)
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.Suppliers')
              AND is_primary_key = 0
            ORDER BY name
            """);
        Assert.Equal(
            [
                "IX_Suppliers_LastEditedAtUtc|0",
                "IX_Suppliers_LegalName|0",
                "IX_Suppliers_TaxId|1",
            ],
            indexes);

        var checks = await ReadStringsAsync(
            connection,
            """
            SELECT name
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.Suppliers')
            ORDER BY name
            """);
        Assert.Equal(
            ["CK_Suppliers_AnnualBillingUsd", "CK_Suppliers_TaxId"],
            checks);

        var screeningTables = await ReadStringsAsync(
            connection,
            """
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = 'screening'
            ORDER BY TABLE_NAME
            """);
        Assert.Equal(
            ["ScreeningMatches", "ScreeningRuns", "ScreeningSourceResults"],
            screeningTables);
    }

    [Fact]
    public async Task SqlConstraintsRejectInvalidTaxIdNegativeBillingAndDuplicates()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await InsertAsync(connection, "20555555555", 1m);

        _ = await Assert.ThrowsAsync<SqlException>(() =>
            InsertAsync(connection, "20555555555", 1m));
        _ = await Assert.ThrowsAsync<SqlException>(() =>
            InsertAsync(connection, "ABC55555555", 1m));
        _ = await Assert.ThrowsAsync<SqlException>(() =>
            InsertAsync(connection, "20666666666", -1m));
    }

    [Fact]
    public async Task StoreTranslatesConcurrentUniqueConstraintConflict()
    {
        using var factory = new IdentityApiFactory(
            _connectionString,
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 25, 3, 0, 0, TimeSpan.Zero)));
        var first = Supplier("20777777777");
        var second = Supplier("20777777777") with { Id = Guid.NewGuid() };

        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var firstStore = firstScope.ServiceProvider
            .GetRequiredService<ISupplierStore>();
        var secondStore = secondScope.ServiceProvider
            .GetRequiredService<ISupplierStore>();
        var outcomes = await Task.WhenAll(
            firstStore.CreateAsync(
                first,
                TestContext.Current.CancellationToken),
            secondStore.CreateAsync(
                second,
                TestContext.Current.CancellationToken));

        Assert.Contains(SupplierStoreWriteOutcome.Saved, outcomes);
        Assert.Contains(SupplierStoreWriteOutcome.TaxIdConflict, outcomes);
    }

    private static Supplier Supplier(string taxId) =>
        new(
            Guid.NewGuid(),
            "Constraint Supplier",
            "Constraint",
            taxId,
            "+51 999 999 999",
            "constraint@example.com",
            "https://example.com",
            "Av. Constraint 123",
            "Peru",
            100m,
            new DateTimeOffset(2026, 7, 25, 3, 0, 0, TimeSpan.Zero));

    private static async Task InsertAsync(
        SqlConnection connection,
        string taxId,
        decimal annualBilling)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO dbo.Suppliers (
                Id, LegalName, CommercialName, TaxId, PhoneNumber, Email,
                Website, PhysicalAddress, Country, AnnualBillingUsd,
                LastEditedAtUtc)
            VALUES (
                NEWID(), N'Constraint Supplier', N'Constraint', @TaxId,
                N'+51 999 999 999', N'constraint@example.com',
                N'https://example.com', N'Av. Constraint 123', N'Peru',
                @AnnualBillingUsd, '2026-07-25T03:00:00+00:00')
            """;
        _ = command.Parameters.AddWithValue("@TaxId", taxId);
        _ = command.Parameters.AddWithValue(
            "@AnnualBillingUsd",
            annualBilling);
        _ = await command.ExecuteNonQueryAsync(
            TestContext.Current.CancellationToken);
    }

    private static async Task<string[]> ReadStringsAsync(
        SqlConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }
}
