using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Infrastructure.Persistence.Screening.Entities;

namespace EyRiskScreening.Infrastructure.Persistence.Screening;

internal static class ScreeningRunPersistenceMapper
{
    public static ScreeningRunEntity ToEntity(
        ScreeningRun run,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = new ScreeningRunEntity
        {
            RunId = run.RunId,
            UserId = run.UserId,
            EntityName = run.EntityName,
            NormalizedEntityName = run.NormalizedEntityName,
            RequestedAtUtc = run.RequestedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            TotalDurationMs = ToMilliseconds(run.TotalDuration),
            Status = run.Status,
            TotalHits = run.TotalHits,
            TotalReturnedResults = run.TotalReturnedResults,
        };

        foreach (var source in run.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceEntity = new ScreeningSourceResultEntity
            {
                RunId = run.RunId,
                Source = source.Source,
                Status = source.Status,
                MatchThreshold = checked((byte)source.MatchThreshold),
                Hits = source.Hits,
                ReturnedResults = source.ReturnedResults,
                DurationMs = ToMilliseconds(source.Duration),
                ErrorCode = source.ErrorCode,
                ErrorMessage = source.ErrorMessage,
            };

            foreach (var match in source.Matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sourceEntity.Matches.Add(new ScreeningMatchEntity
                {
                    SortOrder = match.SortOrder,
                    ReferenceId = match.ReferenceId,
                    Name = match.Name,
                    NormalizedName = match.NormalizedName,
                    OverallScore = match.OverallScore,
                    TokenSimilarity = match.TokenSimilarity,
                    EditSimilarity = match.EditSimilarity,
                    IsExactMatch = match.IsExactMatch,
                    FieldsJson = ScreeningMatchFieldsJsonSerializer.Serialize(
                        match.Fields,
                        cancellationToken),
                });
            }

            entity.Sources.Add(sourceEntity);
        }

        return entity;
    }

    public static ScreeningRun ToDomain(
        ScreeningRunEntity entity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new List<ScreeningSourceExecution>(entity.Sources.Count);
        foreach (var source in entity.Sources.OrderBy(item => item.Source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = new List<ScreeningMatchSnapshot>(source.Matches.Count);
            foreach (var match in source.Matches.OrderBy(item => item.SortOrder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                matches.Add(new ScreeningMatchSnapshot(
                    match.SortOrder,
                    match.ReferenceId,
                    match.Name,
                    match.NormalizedName,
                    match.OverallScore,
                    match.TokenSimilarity,
                    match.EditSimilarity,
                    match.IsExactMatch,
                    ScreeningMatchFieldsJsonSerializer.Deserialize(
                        match.FieldsJson,
                        cancellationToken)));
            }

            sources.Add(new ScreeningSourceExecution(
                source.Source,
                source.Status,
                source.MatchThreshold,
                source.Hits,
                source.ReturnedResults,
                TimeSpan.FromMilliseconds(source.DurationMs),
                source.ErrorCode,
                source.ErrorMessage,
                matches));
        }

        return new ScreeningRun(
            entity.RunId,
            entity.UserId,
            entity.EntityName,
            entity.NormalizedEntityName,
            entity.RequestedAtUtc,
            entity.CompletedAtUtc,
            TimeSpan.FromMilliseconds(entity.TotalDurationMs),
            entity.Status,
            entity.TotalHits,
            entity.TotalReturnedResults,
            sources);
    }

    private static long ToMilliseconds(TimeSpan duration) =>
        checked((long)Math.Round(
            duration.TotalMilliseconds,
            MidpointRounding.AwayFromZero));
}
