using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal sealed class OffshoreLeaksCache : IDisposable
{
    private const string ContractVersion = "v1";
    private readonly object _sync = new();
    private readonly SemaphoreSlim _entityRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Dictionary<string, QueryCacheEntry> _queryEntries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<
        string,
        TaskCompletionSource<IReadOnlyList<ScreeningSourceCandidate>>>
        _queryFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<EntityCacheKey, EntityCacheEntry> _entityEntries =
        [];
    private readonly HashSet<Task> _workers = [];
    private readonly OffshoreLeaksAdapterOptions _options;
    private readonly TimeSpan _queryTtl;
    private readonly TimeSpan _entityTtl;
    private readonly TimeSpan _refreshTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private long _sequence;
    private int _activeEntityOperations;
    private int _disposed;
    private int _resourcesDisposed;

    public OffshoreLeaksCache(
        IOptions<OffshoreLeaksAdapterOptions> optionsAccessor,
        ScreeningOptions screeningOptions,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime)
    {
        _options = optionsAccessor.Value;
        _queryTtl = TimeSpan.FromMinutes(_options.QueryCacheTtlMinutes);
        _entityTtl = TimeSpan.FromHours(_options.EntityCacheTtlHours);
        _refreshTimeout = TimeSpan.FromSeconds(
            screeningOptions.Sources[ScreeningSource.OffshoreLeaks]
                .TimeoutSeconds);
        _timeProvider = timeProvider;
        _applicationLifetime = applicationLifetime;
    }

    public static string CreateQueryKey(string normalizedEntityName)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{ContractVersion}|Entity|{normalizedEntityName}"));
        return Convert.ToHexString(bytes);
    }

    public async Task<OffshoreLeaksQueryCacheResult> GetOrCreateQueryAsync(
        string key,
        Func<CancellationToken, Task<IReadOnlyList<ScreeningSourceCandidate>>>
            factory,
        CancellationToken callerCancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        callerCancellationToken.ThrowIfCancellationRequested();

        Task<IReadOnlyList<ScreeningSourceCandidate>> sharedTask;
        var cacheHit = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (TryGetFreshQuery(key, out var cached))
            {
                return new OffshoreLeaksQueryCacheResult(cached, true);
            }

            if (_queryFlights.TryGetValue(key, out var existing))
            {
                sharedTask = existing.Task;
            }
            else
            {
                var completion = new TaskCompletionSource<
                    IReadOnlyList<ScreeningSourceCandidate>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                ObserveFault(completion.Task);
                _queryFlights.Add(key, completion);
                var worker = RunQueryFlightAsync(
                    key,
                    completion,
                    factory);
                TrackWorker(worker);
                sharedTask = completion.Task;
            }
        }

        var candidates = await sharedTask
            .WaitAsync(callerCancellationToken)
            .ConfigureAwait(false);
        callerCancellationToken.ThrowIfCancellationRequested();
        return new OffshoreLeaksQueryCacheResult(candidates, cacheHit);
    }

    public async Task<IReadOnlyList<IcijEnrichedEntity>>
        GetOrCreateEntitiesAsync(
            IcijNamespaceDefinition @namespace,
            IReadOnlyList<IcijCandidate> candidates,
            OffshoreLeaksRequestBudget requestBudget,
            Func<
                IReadOnlyList<IcijCandidate>,
                OffshoreLeaksRequestBudget,
                CancellationToken,
                Task<IReadOnlyList<IcijEnrichedEntity>>> factory,
            CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            _activeEntityOperations++;
        }

        try
        {
            using var linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _applicationLifetime.ApplicationStopping,
                    _disposeCancellation.Token);
            var operationToken = linkedCancellation.Token;
            operationToken.ThrowIfCancellationRequested();
            var result = FreshEntities(@namespace.Value, candidates);
            if (result.Count == candidates.Count)
            {
                return OrderEntities(candidates, result);
            }

            await _entityRefreshGate
                .WaitAsync(operationToken)
                .ConfigureAwait(false);
            try
            {
                result = FreshEntities(@namespace.Value, candidates);
                var missing = candidates
                    .Where(candidate => !result.ContainsKey(candidate.NodeId))
                    .ToArray();
                if (missing.Length == 0)
                {
                    return OrderEntities(candidates, result);
                }

                var fetched = new List<IcijEnrichedEntity>(missing.Length);
                foreach (var batch in missing.Chunk(_options.MaxExtensionIds))
                {
                    operationToken.ThrowIfCancellationRequested();
                    var entities = await factory(
                            batch,
                            requestBudget,
                            operationToken)
                        .ConfigureAwait(false);
                    operationToken.ThrowIfCancellationRequested();
                    ValidateBatch(@namespace.Value, batch, entities);
                    fetched.AddRange(entities);
                }

                AddEntitiesAtomically(fetched, operationToken);
                foreach (var entity in fetched)
                {
                    result[entity.NodeId] = entity;
                }

                return OrderEntities(candidates, result);
            }
            finally
            {
                _ = _entityRefreshGate.Release();
            }
        }
        finally
        {
            lock (_sync)
            {
                _activeEntityOperations--;
                TryDisposeResources();
            }
        }
    }

    private async Task RunQueryFlightAsync(
        string key,
        TaskCompletionSource<IReadOnlyList<ScreeningSourceCandidate>> completion,
        Func<CancellationToken, Task<IReadOnlyList<ScreeningSourceCandidate>>>
            factory)
    {
        using var timeoutCancellation = new CancellationTokenSource(
            _refreshTimeout,
            _timeProvider);
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellation.Token,
                _applicationLifetime.ApplicationStopping,
                _disposeCancellation.Token);
        try
        {
            var candidates = await factory(linkedCancellation.Token)
                .ConfigureAwait(false);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            var immutable = new ReadOnlyCollection<ScreeningSourceCandidate>(
                candidates.ToArray());
            lock (_sync)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                AddQueryEntry(key, immutable);
                _ = completion.TrySetResult(immutable);
            }
        }
        catch (OperationCanceledException exception)
            when (_applicationLifetime.ApplicationStopping.IsCancellationRequested
                  || _disposeCancellation.IsCancellationRequested)
        {
            _ = completion.TrySetException(exception);
        }
        catch (OperationCanceledException exception)
            when (timeoutCancellation.IsCancellationRequested)
        {
            _ = completion.TrySetException(
                new ScreeningSourceTimedOutException(
                    "The shared Offshore Leaks query reached its configured time limit.",
                    exception));
        }
        catch (Exception exception)
        {
            _ = completion.TrySetException(exception);
        }
        finally
        {
            lock (_sync)
            {
                _queryFlights.Remove(key);
            }
        }
    }

    private bool TryGetFreshQuery(
        string key,
        out IReadOnlyList<ScreeningSourceCandidate> candidates)
    {
        if (_queryEntries.TryGetValue(key, out var entry)
            && _timeProvider.GetUtcNow() < entry.ExpiresAtUtc)
        {
            candidates = entry.Candidates;
            return true;
        }

        _queryEntries.Remove(key);
        candidates = [];
        return false;
    }

    private Dictionary<long, IcijEnrichedEntity> FreshEntities(
        IcijNamespace @namespace,
        IReadOnlyList<IcijCandidate> candidates)
    {
        var now = _timeProvider.GetUtcNow();
        var result = new Dictionary<long, IcijEnrichedEntity>();
        lock (_sync)
        {
            foreach (var candidate in candidates)
            {
                var key = new EntityCacheKey(@namespace, candidate.NodeId);
                if (_entityEntries.TryGetValue(key, out var entry)
                    && now < entry.ExpiresAtUtc)
                {
                    result.Add(candidate.NodeId, entry.Entity);
                }
                else
                {
                    _entityEntries.Remove(key);
                }
            }
        }

        return result;
    }

    private void AddQueryEntry(
        string key,
        IReadOnlyList<ScreeningSourceCandidate> candidates)
    {
        var sequence = checked(++_sequence);
        _queryEntries[key] = new QueryCacheEntry(
            candidates,
            _timeProvider.GetUtcNow().Add(_queryTtl),
            sequence);
        while (_queryEntries.Count > _options.QueryCacheMaxEntries)
        {
            var oldest = _queryEntries.MinBy(pair => pair.Value.Sequence).Key;
            _queryEntries.Remove(oldest);
        }
    }

    private void AddEntitiesAtomically(
        IReadOnlyList<IcijEnrichedEntity> entities,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expiresAtUtc = _timeProvider.GetUtcNow().Add(_entityTtl);
            foreach (var entity in entities)
            {
                var sequence = checked(++_sequence);
                _entityEntries[
                    new EntityCacheKey(entity.Namespace, entity.NodeId)] =
                    new EntityCacheEntry(entity, expiresAtUtc, sequence);
            }

            while (_entityEntries.Count > _options.EntityCacheMaxEntries)
            {
                var oldest = _entityEntries
                    .MinBy(pair => pair.Value.Sequence)
                    .Key;
                _entityEntries.Remove(oldest);
            }
        }
    }

    private static void ValidateBatch(
        IcijNamespace @namespace,
        IReadOnlyList<IcijCandidate> candidates,
        IReadOnlyList<IcijEnrichedEntity> entities)
    {
        var expectedIds = candidates
            .Select(candidate => candidate.NodeId)
            .Order()
            .ToArray();
        var actualIds = entities
            .Where(entity => entity.Namespace == @namespace)
            .Select(entity => entity.NodeId)
            .Order()
            .ToArray();
        if (!expectedIds.SequenceEqual(actualIds))
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ extension result does not match its requested identifiers.");
        }
    }

    private static IcijEnrichedEntity[] OrderEntities(
        IReadOnlyList<IcijCandidate> candidates,
        Dictionary<long, IcijEnrichedEntity> entities) =>
        candidates.Select(candidate => entities[candidate.NodeId]).ToArray();

    private void TrackWorker(Task worker)
    {
        _workers.Add(worker);
        _ = worker.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (_sync)
                {
                    _workers.Remove(completed);
                    TryDisposeResources();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCancellation.Cancel();
        lock (_sync)
        {
            _queryEntries.Clear();
            _entityEntries.Clear();
            TryDisposeResources();
        }
    }

    private void TryDisposeResources()
    {
        if (Volatile.Read(ref _disposed) == 0
            || _workers.Count != 0
            || _activeEntityOperations != 0
            || Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _entityRefreshGate.Dispose();
        _disposeCancellation.Dispose();
    }

    private sealed record QueryCacheEntry(
        IReadOnlyList<ScreeningSourceCandidate> Candidates,
        DateTimeOffset ExpiresAtUtc,
        long Sequence);

    private sealed record EntityCacheEntry(
        IcijEnrichedEntity Entity,
        DateTimeOffset ExpiresAtUtc,
        long Sequence);

    private readonly record struct EntityCacheKey(
        IcijNamespace Namespace,
        long NodeId);
}

internal sealed record OffshoreLeaksQueryCacheResult(
    IReadOnlyList<ScreeningSourceCandidate> Candidates,
    bool CacheHit);
