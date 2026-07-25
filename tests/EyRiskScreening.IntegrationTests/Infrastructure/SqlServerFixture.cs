using EyRiskScreening.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder(
        "mcr.microsoft.com/mssql/server:2025-CU7-ubuntu-22.04")
        .Build();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public async Task<string> CreateMigratedDatabaseAsync(
        string databaseName,
        CancellationToken cancellationToken)
    {
        var connectionStringBuilder = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = databaseName,
        };

        var connectionString = connectionStringBuilder.ConnectionString;
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.MigrateAsync(cancellationToken);

        return connectionString;
    }
}
