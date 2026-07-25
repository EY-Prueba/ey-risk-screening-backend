using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EyRiskScreening.Infrastructure.Persistence;

public sealed class ApplicationDbContextFactory
    : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer()
            .Options;

        return new ApplicationDbContext(options);
    }
}
