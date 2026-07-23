using EyRiskScreening.Application.Screening.History;
using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Infrastructure.Persistence.Screening.Entities;
using Microsoft.EntityFrameworkCore;

namespace EyRiskScreening.Infrastructure.Persistence.Screening;

internal sealed class ScreeningRunStore(ApplicationDbContext context)
    : IScreeningRunStore
{
    public async Task SaveAsync(
        ScreeningRun run,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = ScreeningRunPersistenceMapper.ToEntity(run, cancellationToken);
        context.ScreeningRuns.Add(entity);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ScreeningRun?> GetByIdAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var filtered = context.ScreeningRuns
            .AsNoTracking()
            .Where(run => run.RunId == runId);
        return MaterializeAsync(filtered, cancellationToken);
    }

    public Task<ScreeningRun?> GetByIdForUserAsync(
        Guid runId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var filtered = context.ScreeningRuns
            .AsNoTracking()
            .Where(run => run.RunId == runId && run.UserId == userId);
        return MaterializeAsync(filtered, cancellationToken);
    }

    private static async Task<ScreeningRun?> MaterializeAsync(
        IQueryable<ScreeningRunEntity> filtered,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = await filtered
            .AsSplitQuery()
            .Include(run => run.Sources)
            .ThenInclude(source => source.Matches)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return entity is null
            ? null
            : ScreeningRunPersistenceMapper.ToDomain(entity, cancellationToken);
    }
}
