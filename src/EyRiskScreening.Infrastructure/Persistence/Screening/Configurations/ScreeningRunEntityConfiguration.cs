using EyRiskScreening.Infrastructure.Identity;
using EyRiskScreening.Infrastructure.Persistence.Screening.Entities;
using EyRiskScreening.Domain.Screening.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EyRiskScreening.Infrastructure.Persistence.Screening.Configurations;

internal sealed class ScreeningRunEntityConfiguration
    : IEntityTypeConfiguration<ScreeningRunEntity>
{
    public void Configure(EntityTypeBuilder<ScreeningRunEntity> builder)
    {
        builder.ToTable("ScreeningRuns", "screening", table =>
        {
            table.HasCheckConstraint(
                "CK_ScreeningRuns_Status",
                "[Status] IN (0, 1, 2)");
            table.HasCheckConstraint(
                "CK_ScreeningRuns_TotalDurationMs",
                "[TotalDurationMs] >= 0");
            table.HasCheckConstraint(
                "CK_ScreeningRuns_Counts",
                "[TotalHits] >= 0 AND [TotalReturnedResults] >= 0 "
                + "AND [TotalReturnedResults] <= [TotalHits]");
            table.HasCheckConstraint(
                "CK_ScreeningRuns_Timestamps",
                "[CompletedAtUtc] >= [RequestedAtUtc]");
        });

        builder.HasKey(run => run.RunId);
        builder
            .Property(run => run.EntityName)
            .HasMaxLength(ScreeningHistoryLimits.EntityNameSqlLength)
            .IsRequired();
        builder
            .Property(run => run.NormalizedEntityName)
            .HasMaxLength(ScreeningHistoryLimits.EntityNameSqlLength)
            .IsRequired();
        builder.Property(run => run.RequestedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(run => run.CompletedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(run => run.Status).HasConversion<byte>();

        builder
            .HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(run => run.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder
            .HasMany(run => run.Sources)
            .WithOne(source => source.Run)
            .HasForeignKey(source => source.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(run => new { run.UserId, run.RequestedAtUtc })
            .IsDescending(false, true);
        builder
            .HasIndex(run => run.RequestedAtUtc)
            .IsDescending();
    }
}
