using EyRiskScreening.Domain.Security;
using EyRiskScreening.Infrastructure.Identity;
using EyRiskScreening.Infrastructure.Persistence.Screening.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EyRiskScreening.Infrastructure.Persistence;

public sealed class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    private static readonly Guid AdminRoleId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AnalystRoleId = new("22222222-2222-2222-2222-222222222222");

    internal DbSet<ScreeningRunEntity> ScreeningRuns => Set<ScreeningRunEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        builder.Entity<IdentityRole<Guid>>().HasData(
            new IdentityRole<Guid>
            {
                Id = AdminRoleId,
                Name = RoleNames.Admin,
                NormalizedName = RoleNames.Admin.ToUpperInvariant(),
                ConcurrencyStamp = "admin-role-v1",
            },
            new IdentityRole<Guid>
            {
                Id = AnalystRoleId,
                Name = RoleNames.Analyst,
                NormalizedName = RoleNames.Analyst.ToUpperInvariant(),
                ConcurrencyStamp = "analyst-role-v1",
            });
    }
}
