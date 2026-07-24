using System.Collections.ObjectModel;
using System.Globalization;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal sealed partial class OffshoreLeaksScreeningSourceAdapter(
    IIcijReconciliationClient reconciliationClient,
    IIcijExtensionClient extensionClient,
    OffshoreLeaksCache cache,
    IOptions<OffshoreLeaksAdapterOptions> optionsAccessor,
    TimeProvider timeProvider,
    ILogger<OffshoreLeaksScreeningSourceAdapter> logger)
    : IScreeningSourceAdapter
{
    private readonly OffshoreLeaksAdapterOptions _options = optionsAccessor.Value;

    public ScreeningSource Source => ScreeningSource.OffshoreLeaks;

    public async Task<IReadOnlyList<ScreeningSourceCandidate>> SearchAsync(
        ScreeningSourceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = OffshoreLeaksCache.CreateQueryKey(
            query.NormalizedEntityName);
        var cached = await cache.GetOrCreateQueryAsync(
                cacheKey,
                token => RefreshAsync(query.EntityName, token),
                cancellationToken)
            .ConfigureAwait(false);
        LogCacheOutcome(logger, cached.CacheHit);
        return cached.Candidates;
    }

    private async Task<IReadOnlyList<ScreeningSourceCandidate>> RefreshAsync(
        string entityName,
        CancellationToken cancellationToken)
    {
        LogRefreshStarted(logger, IcijNamespaces.All.Count);
        var startedAt = timeProvider.GetTimestamp();
        var requestBudget = new OffshoreLeaksRequestBudget(
            _options.MaxRequestsPerQuery);
        var searchTasks = IcijNamespaces.All
            .Select(async definition =>
            {
                var candidates = await reconciliationClient
                    .SearchAsync(
                        definition,
                        entityName,
                        requestBudget,
                        cancellationToken)
                    .ConfigureAwait(false);
                LogNamespaceCandidateCount(
                    logger,
                    definition.Value,
                    candidates.Count);
                return new NamespaceCandidates(definition, candidates);
            })
            .ToArray();
        var namespaceCandidates = await Task.WhenAll(searchTasks)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var beforeDeduplication = namespaceCandidates.Sum(
            result => result.Candidates.Count);
        if (beforeDeduplication > _options.MaxCandidatesBeforeDeduplication)
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ query exceeded its global candidate limit.");
        }

        var enriched = new List<IcijEnrichedEntity>(
            beforeDeduplication);
        foreach (var result in namespaceCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Candidates.Count == 0)
            {
                continue;
            }

            var entities = await cache.GetOrCreateEntitiesAsync(
                    result.Definition,
                    result.Candidates,
                    requestBudget,
                    (batch, budget, token) => extensionClient.EnrichAsync(
                        result.Definition,
                        batch,
                        budget,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            enriched.AddRange(entities);
        }

        var merged = MergeEntities(enriched, cancellationToken);
        var dataRetrievedAtUtc = timeProvider.GetUtcNow();
        var candidates = merged
            .Select(entity => CreateCandidate(
                entity,
                dataRetrievedAtUtc,
                cancellationToken))
            .OrderBy(candidate => candidate.ReferenceId, StringComparer.Ordinal)
            .ToArray();
        var duration = timeProvider.GetElapsedTime(startedAt);
        LogRefreshCompleted(
            logger,
            beforeDeduplication,
            candidates.Length,
            IcijNamespaces.All.Count,
            requestBudget.Consumed,
            duration.TotalMilliseconds);
        return new ReadOnlyCollection<ScreeningSourceCandidate>(candidates);
    }

    private static OffshoreLeaksMergedEntity[] MergeEntities(
        IReadOnlyList<IcijEnrichedEntity> entities,
        CancellationToken cancellationToken)
    {
        var merged = new Dictionary<long, MutableMergedEntity>();
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!merged.TryGetValue(entity.NodeId, out var current))
            {
                merged.Add(
                    entity.NodeId,
                    new MutableMergedEntity(entity));
                continue;
            }

            current.Merge(entity);
        }

        return merged.Values
            .OrderBy(value => value.NodeId)
            .Select(value => value.ToImmutable())
            .ToArray();
    }

    private static ScreeningSourceCandidate CreateCandidate(
        OffshoreLeaksMergedEntity entity,
        DateTimeOffset dataRetrievedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fields = new List<ScreeningSourceField>();
        AddIfPresent(fields, "Jurisdiction", entity.Jurisdiction);
        var linkedTo = OffshoreLeaksBoundedFieldProjector.Project(
            entity.LinkedTo,
            entity.LinkedToOmittedCount,
            cancellationToken);
        AddIfPresent(fields, "LinkedTo", linkedTo.Value);
        if (linkedTo.TotalCount > 1 || linkedTo.OmittedCount > 0)
        {
            fields.Add(new ScreeningSourceField(
                "LinkedToCount",
                linkedTo.TotalCountText));
        }

        if (linkedTo.OmittedCount > 0)
        {
            fields.Add(new ScreeningSourceField(
                "LinkedToOmittedCount",
                linkedTo.OmittedCountText));
        }

        var dataFromValues = IcijNamespaces.All
            .Where(definition => entity.Namespaces.Contains(definition.Value))
            .Select(definition => definition.DataFrom)
            .ToArray();
        fields.Add(new ScreeningSourceField(
            "DataFrom",
            string.Join("; ", dataFromValues)));
        if (dataFromValues.Length > 1)
        {
            fields.Add(new ScreeningSourceField(
                "DataFromCount",
                dataFromValues.Length.ToString(CultureInfo.InvariantCulture)));
        }

        AddIfPresent(fields, "IcijId", entity.IcijId);
        fields.Add(new ScreeningSourceField("SchemaType", "Entity"));
        fields.Add(new ScreeningSourceField(
            "DataRetrievedAtUtc",
            dataRetrievedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
        ValidatePersistable(
            entity.NodeId,
            entity.Name,
            fields);
        return new ScreeningSourceCandidate(
            $"icij:{entity.NodeId.ToString(CultureInfo.InvariantCulture)}",
            entity.Name,
            new ReadOnlyCollection<ScreeningSourceField>(fields));
    }

    private static void AddIfPresent(
        List<ScreeningSourceField> fields,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields.Add(new ScreeningSourceField(name, value));
        }
    }

    private static void ValidatePersistable(
        long nodeId,
        string name,
        List<ScreeningSourceField> fields)
    {
        try
        {
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                $"icij:{nodeId.ToString(CultureInfo.InvariantCulture)}",
                name,
                fields);
        }
        catch (ScreeningHistoryValidationException exception)
        {
            throw new OffshoreLeaksAdapterException(
                "An Offshore Leaks result exceeds the screening history limits.",
                exception);
        }
    }

    private static bool Compatible(string first, string second) =>
        string.Equals(
            EntityNameNormalizer.Normalize(first).Value,
            EntityNameNormalizer.Normalize(second).Value,
            StringComparison.Ordinal);

    [LoggerMessage(
        EventId = 4120,
        Level = LogLevel.Information,
        Message = "Offshore Leaks query refresh started for {NamespaceCount} namespaces.")]
    private static partial void LogRefreshStarted(
        ILogger logger,
        int namespaceCount);

    [LoggerMessage(
        EventId = 4121,
        Level = LogLevel.Information,
        Message = "Offshore Leaks query refresh completed with {CandidatesBeforeDeduplication} candidates before and {CandidatesAfterDeduplication} after deduplication across {NamespaceCount} namespaces using {RequestCount} requests in {DurationMilliseconds} ms.")]
    private static partial void LogRefreshCompleted(
        ILogger logger,
        int candidatesBeforeDeduplication,
        int candidatesAfterDeduplication,
        int namespaceCount,
        int requestCount,
        double durationMilliseconds);

    [LoggerMessage(
        EventId = 4122,
        Level = LogLevel.Information,
        Message = "Offshore Leaks query cache outcome: hit={CacheHit}.")]
    private static partial void LogCacheOutcome(
        ILogger logger,
        bool cacheHit);

    [LoggerMessage(
        EventId = 4123,
        Level = LogLevel.Debug,
        Message = "Offshore Leaks namespace {Namespace} returned {CandidateCount} candidates.")]
    private static partial void LogNamespaceCandidateCount(
        ILogger logger,
        IcijNamespace @namespace,
        int candidateCount);

    private sealed record NamespaceCandidates(
        IcijNamespaceDefinition Definition,
        IReadOnlyList<IcijCandidate> Candidates);

    private sealed class MutableMergedEntity
    {
        private readonly HashSet<string> _linkedTo =
            new(StringComparer.Ordinal);
        private readonly HashSet<IcijNamespace> _namespaces = [];

        public MutableMergedEntity(IcijEnrichedEntity entity)
        {
            NodeId = entity.NodeId;
            Name = entity.Name;
            Jurisdiction = entity.Jurisdiction;
            IcijId = entity.IcijId;
            LinkedToOmittedCount = entity.LinkedToOmittedCount;
            _linkedTo.UnionWith(entity.LinkedTo);
            _namespaces.Add(entity.Namespace);
        }

        public long NodeId { get; }

        public string Name { get; }

        public string? Jurisdiction { get; private set; }

        public string? IcijId { get; private set; }

        public int LinkedToOmittedCount { get; private set; }

        public void Merge(IcijEnrichedEntity entity)
        {
            if (!Compatible(Name, entity.Name)
                || Incompatible(Jurisdiction, entity.Jurisdiction)
                || Incompatible(IcijId, entity.IcijId))
            {
                throw new OffshoreLeaksAdapterException(
                    "ICIJ namespaces contain conflicting values for one node.");
            }

            Jurisdiction ??= entity.Jurisdiction;
            IcijId ??= entity.IcijId;
            _linkedTo.UnionWith(entity.LinkedTo);
            LinkedToOmittedCount = checked(
                LinkedToOmittedCount + entity.LinkedToOmittedCount);
            _namespaces.Add(entity.Namespace);
        }

        public OffshoreLeaksMergedEntity ToImmutable() =>
            new(
                NodeId,
                Name,
                Jurisdiction,
                new ReadOnlyCollection<string>(
                    _linkedTo.OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray()),
                LinkedToOmittedCount,
                IcijId,
                new ReadOnlyCollection<IcijNamespace>(
                    IcijNamespaces.All
                        .Where(definition =>
                            _namespaces.Contains(definition.Value))
                        .Select(definition => definition.Value)
                        .ToArray()));

        private static bool Incompatible(string? first, string? second) =>
            first is not null
            && second is not null
            && !Compatible(first, second);
    }
}
