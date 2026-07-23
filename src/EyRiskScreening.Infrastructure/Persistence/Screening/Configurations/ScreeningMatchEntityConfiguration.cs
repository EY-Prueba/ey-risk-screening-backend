using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Infrastructure.Persistence.Screening.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EyRiskScreening.Infrastructure.Persistence.Screening.Configurations;

internal sealed class ScreeningMatchEntityConfiguration
    : IEntityTypeConfiguration<ScreeningMatchEntity>
{
    public void Configure(EntityTypeBuilder<ScreeningMatchEntity> builder)
    {
        builder.ToTable("ScreeningMatches", "screening", table =>
        {
            table.HasCheckConstraint(
                "CK_ScreeningMatches_SortOrder",
                "[SortOrder] >= 0");
            table.HasCheckConstraint(
                "CK_ScreeningMatches_Scores",
                "[OverallScore] BETWEEN 0 AND 100 "
                + "AND [TokenSimilarity] BETWEEN 0 AND 100 "
                + "AND [EditSimilarity] BETWEEN 0 AND 100");
            table.HasCheckConstraint(
                "CK_ScreeningMatches_ExactScore",
                "[IsExactMatch] = 0 OR [OverallScore] = 100");
            table.HasCheckConstraint(
                "CK_ScreeningMatches_FieldsJson",
                "ISJSON([FieldsJson]) = 1 AND DATALENGTH([FieldsJson]) <= 131072");
        });

        builder.HasKey(match => match.ScreeningMatchId);
        builder
            .Property(match => match.ReferenceId)
            .HasMaxLength(ScreeningHistoryLimits.ReferenceIdSqlLength)
            .IsRequired();
        builder
            .Property(match => match.Name)
            .HasMaxLength(ScreeningHistoryLimits.MatchNameSqlLength)
            .IsRequired();
        builder
            .Property(match => match.NormalizedName)
            .HasMaxLength(ScreeningHistoryLimits.MatchNameSqlLength)
            .IsRequired();
        builder.Property(match => match.OverallScore).HasPrecision(5, 2);
        builder.Property(match => match.TokenSimilarity).HasPrecision(5, 2);
        builder.Property(match => match.EditSimilarity).HasPrecision(5, 2);
        builder.Property(match => match.FieldsJson).HasColumnType("nvarchar(max)").IsRequired();

        builder
            .HasIndex(match => new { match.ScreeningSourceResultId, match.SortOrder })
            .IsUnique();
    }
}
