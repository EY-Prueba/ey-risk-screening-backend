using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Infrastructure.Persistence.Screening.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EyRiskScreening.Infrastructure.Persistence.Screening.Configurations;

internal sealed class ScreeningSourceResultEntityConfiguration
    : IEntityTypeConfiguration<ScreeningSourceResultEntity>
{
    public void Configure(EntityTypeBuilder<ScreeningSourceResultEntity> builder)
    {
        builder.ToTable("ScreeningSourceResults", "screening", table =>
        {
            table.HasCheckConstraint(
                "CK_ScreeningSourceResults_Source",
                "[Source] IN (0, 1, 2)");
            table.HasCheckConstraint(
                "CK_ScreeningSourceResults_Status",
                "[Status] IN (0, 1, 2, 3)");
            table.HasCheckConstraint(
                "CK_ScreeningSourceResults_ErrorCode",
                "[ErrorCode] IS NULL OR [ErrorCode] IN (0, 1, 2, 3)");
            table.HasCheckConstraint(
                "CK_ScreeningSourceResults_MatchThreshold",
                "[MatchThreshold] BETWEEN 0 AND 100");
            table.HasCheckConstraint(
                "CK_ScreeningSourceResults_Counts",
                "[Hits] >= 0 AND [ReturnedResults] >= 0 "
                + "AND [ReturnedResults] <= [Hits]");
            table.HasCheckConstraint(
                "CK_ScreeningSourceResults_DurationMs",
                "[DurationMs] >= 0");
            table.HasCheckConstraint(
                "CK_ScreeningSourceResults_Outcome",
                "([Status] = 0 AND [ErrorCode] IS NULL AND [ErrorMessage] IS NULL) "
                + "OR ([Status] = 1 AND [ErrorCode] IN (0, 1) AND [ErrorMessage] IS NOT NULL) "
                + "OR ([Status] = 2 AND [ErrorCode] = 2 AND [ErrorMessage] IS NOT NULL) "
                + "OR ([Status] = 3 AND [ErrorCode] = 3 AND [ErrorMessage] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_ScreeningSourceResults_UnsuccessfulCounts",
                "[Status] = 0 OR ([Hits] = 0 AND [ReturnedResults] = 0)");
        });

        builder.HasKey(source => source.ScreeningSourceResultId);
        builder.Property(source => source.Source).HasConversion<byte>();
        builder.Property(source => source.Status).HasConversion<byte>();
        builder.Property(source => source.ErrorCode).HasConversion<byte>();
        builder
            .Property(source => source.ErrorMessage)
            .HasMaxLength(ScreeningHistoryLimits.ErrorMessageSqlLength);

        builder
            .HasMany(source => source.Matches)
            .WithOne(match => match.SourceResult)
            .HasForeignKey(match => match.ScreeningSourceResultId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(source => new { source.RunId, source.Source })
            .IsUnique();
    }
}
