using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OffshoreLeaksAdapterTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task QueriesFiveNamespacesExtendsAndMergesByNodeId()
    {
        using var handler = CompleteHandler(101, "Acme");
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var adapter = CreateAdapter(handler, time, lifetime);

        var candidates = await adapter.Value.SearchAsync(
            new ScreeningSourceQuery("Acme", "ACME"),
            TestContext.Current.CancellationToken);

        var candidate = Assert.Single(candidates);
        Assert.Equal("icij:101", candidate.ReferenceId);
        Assert.Equal("Acme", candidate.Name);
        AssertField(candidate, "Jurisdiction", "British Virgin Islands");
        AssertField(candidate, "LinkedTo", "United Kingdom; United States");
        AssertField(candidate, "DataFrom",
            "Bahamas Leaks; Offshore Leaks; Panama Papers; Pandora Papers; Paradise Papers");
        AssertField(candidate, "DataFromCount", "5");
        AssertField(candidate, "SchemaType", "Entity");
        AssertField(candidate, "DataRetrievedAtUtc", InitialTime.ToString("O"));
        Assert.Equal(10, handler.Requests.Count);
        Assert.Equal(
            [
                "/api/v1/reconcile/bahamas-leaks",
                "/api/v1/reconcile/offshore-leaks",
                "/api/v1/reconcile/panama-papers",
                "/api/v1/reconcile/pandora-papers",
                "/api/v1/reconcile/paradise-papers",
            ],
            handler.Requests
                .Where(request => request.Method == HttpMethod.Post)
                .Select(request => request.Uri.AbsolutePath)
                .OrderBy(path => path, StringComparer.Ordinal));
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Uri.AbsolutePath.Contains(
                "/rest/",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryCacheHitPerformsNoHttpAndReturnsSameReference()
    {
        using var handler = CompleteHandler(101, "Acme");
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var adapter = CreateAdapter(handler, time, lifetime);
        var query = new ScreeningSourceQuery("Acme", "ACME");

        var first = await adapter.Value.SearchAsync(
            query,
            TestContext.Current.CancellationToken);
        var requestCount = handler.Requests.Count;
        var second = await adapter.Value.SearchAsync(
            query,
            TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(requestCount, handler.Requests.Count);
    }

    [Fact]
    public async Task UnsearchableNameKeepsOffshoreLeaksSpecificFailure()
    {
        using var handler = CompleteHandler(101, "---");
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var adapter = CreateAdapter(handler, time, lifetime);

        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(() =>
            adapter.Value.SearchAsync(
                new ScreeningSourceQuery("Acme", "ACME"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredQueryReusesFreshEntityCache()
    {
        using var handler = CompleteHandler(101, "Acme");
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        var options = OffshoreLeaksTestData.Options(queryTtlMinutes: 1);
        using var adapter = CreateAdapter(handler, time, lifetime, options);
        var query = new ScreeningSourceQuery("Acme", "ACME");
        _ = await adapter.Value.SearchAsync(
            query,
            TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));

        var refreshed = await adapter.Value.SearchAsync(
            query,
            TestContext.Current.CancellationToken);

        Assert.Single(refreshed);
        Assert.Equal(15, handler.Requests.Count);
        Assert.Equal(
            5,
            handler.Requests.Count(request => request.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task EntityCacheExpiresAndEnforcesItsEntryLimit()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var cache = OffshoreLeaksTestData.Cache(
            OffshoreLeaksTestData.Options(
                entityTtlHours: 1,
                entityMaxEntries: 1),
            time,
            lifetime);
        var calls = 0;
        Task<IReadOnlyList<IcijEnrichedEntity>> Factory(
            IReadOnlyList<IcijCandidate> candidates,
            OffshoreLeaksRequestBudget requestBudget,
            CancellationToken token)
        {
            _ = requestBudget;
            token.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<IcijEnrichedEntity>>(
                candidates.Select(candidate =>
                    IcijEnrichedEntity.Create(
                        IcijNamespace.BahamasLeaks,
                        candidate.NodeId,
                        candidate.Name,
                        null,
                        [],
                        0,
                        null)).ToArray());
        }

        _ = await GetEntitiesAsync(cache, Candidate(1), Factory);
        _ = await GetEntitiesAsync(cache, Candidate(1), Factory);
        Assert.Equal(1, calls);

        time.Advance(TimeSpan.FromHours(2));
        _ = await GetEntitiesAsync(cache, Candidate(1), Factory);
        Assert.Equal(2, calls);

        _ = await GetEntitiesAsync(cache, Candidate(2), Factory);
        _ = await GetEntitiesAsync(cache, Candidate(1), Factory);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task DisposeCancelsActiveEntityRefreshBeforeReleasingResources()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new TestHostApplicationLifetime();
        var cache = OffshoreLeaksTestData.Cache(
            OffshoreLeaksTestData.Options(),
            new MutableTimeProvider(InitialTime),
            lifetime);
        async Task<IReadOnlyList<IcijEnrichedEntity>> Factory(
            IReadOnlyList<IcijCandidate> candidates,
            OffshoreLeaksRequestBudget requestBudget,
            CancellationToken token)
        {
            _ = candidates;
            _ = requestBudget;
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            }
            finally
            {
                finished.SetResult();
            }
        }

        var refresh = GetEntitiesAsync(cache, Candidate(1), Factory);
        await entered.Task;
        cache.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await refresh);
        await finished.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => GetEntitiesAsync(cache, Candidate(1), Factory));
    }

    [Fact]
    public async Task SingleFlightSurvivesLeaderCallerCancellation()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var cache = OffshoreLeaksTestData.Cache(
            OffshoreLeaksTestData.Options(),
            time,
            lifetime);
        var expected = new ReadOnlyCollection<ScreeningSourceCandidate>(
        [
            new ScreeningSourceCandidate("icij:1", "Acme", []),
        ]);
        async Task<IReadOnlyList<ScreeningSourceCandidate>> Factory(
            CancellationToken token)
        {
            _ = Interlocked.Increment(ref factoryCalls);
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return expected;
        }

        using var leaderCancellation = new CancellationTokenSource();
        var leader = cache.GetOrCreateQueryAsync(
            OffshoreLeaksCache.CreateQueryKey("ACME"),
            Factory,
            leaderCancellation.Token);
        await entered.Task;
        var waiter = cache.GetOrCreateQueryAsync(
            OffshoreLeaksCache.CreateQueryKey("ACME"),
            Factory,
            TestContext.Current.CancellationToken);

        leaderCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await leader);
        release.SetResult();
        var result = await waiter;

        Assert.Same(expected[0], Assert.Single(result.Candidates));
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task FailedSingleFlightIsNotCachedAndCanRetry()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var cache = OffshoreLeaksTestData.Cache(
            OffshoreLeaksTestData.Options(),
            time,
            lifetime);
        var calls = 0;
        Task<IReadOnlyList<ScreeningSourceCandidate>> Factory(
            CancellationToken _)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new OffshoreLeaksAdapterException("controlled");
            }

            return Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>(
            [
                new ScreeningSourceCandidate("icij:1", "Acme", []),
            ]);
        }

        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => cache.GetOrCreateQueryAsync(
                OffshoreLeaksCache.CreateQueryKey("ACME"),
                Factory,
                TestContext.Current.CancellationToken));
        var result = await cache.GetOrCreateQueryAsync(
            OffshoreLeaksCache.CreateQueryKey("ACME"),
            Factory,
            TestContext.Current.CancellationToken);

        Assert.Single(result.Candidates);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task QueryCacheIsBoundedAndDoesNotExposeTheSearchedNameInItsKey()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var cache = OffshoreLeaksTestData.Cache(
            OffshoreLeaksTestData.Options(queryMaxEntries: 1),
            time,
            lifetime);
        var calls = 0;
        Task<IReadOnlyList<ScreeningSourceCandidate>> Factory(
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>([]);
        }

        var firstKey = OffshoreLeaksCache.CreateQueryKey("PRIVATE ENTITY");
        Assert.DoesNotContain(
            "PRIVATE",
            firstKey,
            StringComparison.OrdinalIgnoreCase);
        _ = await cache.GetOrCreateQueryAsync(
            firstKey,
            Factory,
            TestContext.Current.CancellationToken);
        _ = await cache.GetOrCreateQueryAsync(
            OffshoreLeaksCache.CreateQueryKey("SECOND"),
            Factory,
            TestContext.Current.CancellationToken);
        _ = await cache.GetOrCreateQueryAsync(
            firstKey,
            Factory,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task SharedRefreshUsesInternalTimeout()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var cache = OffshoreLeaksTestData.Cache(
            OffshoreLeaksTestData.Options(),
            time,
            lifetime);
        async Task<IReadOnlyList<ScreeningSourceCandidate>> Factory(
            CancellationToken token)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
            return [];
        }

        var task = cache.GetOrCreateQueryAsync(
            OffshoreLeaksCache.CreateQueryKey("ACME"),
            Factory,
            TestContext.Current.CancellationToken);
        await entered.Task;
        time.Advance(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<ScreeningSourceTimedOutException>(
            async () => await task);
    }

    [Fact]
    public async Task HostShutdownCancelsAndObservesSharedRefresh()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var cache = OffshoreLeaksTestData.Cache(
            OffshoreLeaksTestData.Options(),
            time,
            lifetime);
        async Task<IReadOnlyList<ScreeningSourceCandidate>> Factory(
            CancellationToken token)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        }

        var task = cache.GetOrCreateQueryAsync(
            OffshoreLeaksCache.CreateQueryKey("ACME"),
            Factory,
            TestContext.Current.CancellationToken);
        await entered.Task;
        lifetime.StopApplication();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await task);
    }

    [Fact]
    public async Task DisposeCancelsAndObservesSharedRefresh()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        var cache = OffshoreLeaksTestData.Cache(
            OffshoreLeaksTestData.Options(),
            time,
            lifetime);
        async Task<IReadOnlyList<ScreeningSourceCandidate>> Factory(
            CancellationToken token)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        }

        var task = cache.GetOrCreateQueryAsync(
            OffshoreLeaksCache.CreateQueryKey("ACME"),
            Factory,
            TestContext.Current.CancellationToken);
        await entered.Task;
        cache.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await task);
    }

    [Fact]
    public async Task DisposedCacheRejectsNewSingleFlightWithoutWaiting()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var cache = OffshoreLeaksTestData.Cache(
            OffshoreLeaksTestData.Options(),
            new MutableTimeProvider(InitialTime),
            lifetime);
        cache.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => cache.GetOrCreateQueryAsync(
                OffshoreLeaksCache.CreateQueryKey("ACME"),
                _ => Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>(
                    []),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AllCancelledWaitersDoNotLeaveSharedWorkRunning()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var cache = OffshoreLeaksTestData.Cache(
            OffshoreLeaksTestData.Options(),
            time,
            lifetime);
        async Task<IReadOnlyList<ScreeningSourceCandidate>> Factory(
            CancellationToken token)
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                return [];
            }
            finally
            {
                finished.SetResult();
            }
        }

        using var callerCancellation = new CancellationTokenSource();
        var waiting = cache.GetOrCreateQueryAsync(
            OffshoreLeaksCache.CreateQueryKey("ACME"),
            Factory,
            callerCancellation.Token);
        await entered.Task;
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await waiting);

        time.Advance(TimeSpan.FromSeconds(20));

        await finished.Task.WaitAsync(
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GlobalHttpConcurrencyNeverExceedsTwo()
    {
        var twoEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        using var handler = new RecordingHttpMessageHandler(
            async (_, token) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    twoEntered.SetResult();
                }

                await release.Task.WaitAsync(token);
                return OffshoreLeaksTestData.JsonResponse(
                    OffshoreLeaksTestData.ReconciliationJson());
            });
        var options = OffshoreLeaksTestData.Options();
        using var gate = new OffshoreLeaksHttpGate(
            Microsoft.Extensions.Options.Options.Create(options));
        var client = new IcijReconciliationClient(
            new FixedHttpClientFactory(handler, options),
            gate,
            Microsoft.Extensions.Options.Options.Create(options),
            TimeProvider.System,
            NullLogger<IcijReconciliationClient>.Instance);
        var tasks = IcijNamespaces.All.Select(definition =>
            client.SearchAsync(
                definition,
                "Acme",
                new OffshoreLeaksRequestBudget(10),
                TestContext.Current.CancellationToken)).ToArray();

        await twoEntered.Task;
        Assert.Equal(2, handler.MaximumActive);
        release.SetResult();
        _ = await Task.WhenAll(tasks);

        Assert.Equal(2, handler.MaximumActive);
    }

    [Fact]
    public async Task HttpGateDisposalLetsTrackedOperationFinishAndRejectsNewWork()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = OffshoreLeaksTestData.Options();
        var gate = new OffshoreLeaksHttpGate(
            Microsoft.Extensions.Options.Options.Create(options));
        var running = gate.ExecuteAsync(
            async token =>
            {
                entered.SetResult();
                return await release.Task.WaitAsync(token);
            },
            TestContext.Current.CancellationToken);
        await entered.Task;

        gate.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => gate.ExecuteAsync(
                _ => Task.FromResult(2),
                TestContext.Current.CancellationToken));
        release.SetResult(1);

        Assert.Equal(1, await running);
    }

    [Fact]
    public async Task RequestBudgetStopsBeforeEleventhRequest()
    {
        using var handler = new RecordingHttpMessageHandler(
            (request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Task.FromResult(
                        OffshoreLeaksTestData.JsonResponse(
                            OffshoreLeaksTestData.ReconciliationJson(
                                (101, "Acme"),
                                (102, "Acme Two")),
                            HttpStatusCode.Created));
                }

                var extend = Uri.UnescapeDataString(
                    request.Uri.Query["?extend=".Length..]);
                using var document = JsonDocument.Parse(extend);
                var rows = document.RootElement
                    .GetProperty("ids")
                    .EnumerateArray()
                    .Select(id => id.GetInt64())
                    .Select(id => (id, id == 101 ? "Acme" : "Acme Two"))
                    .ToArray();
                return Task.FromResult(
                    OffshoreLeaksTestData.JsonResponse(
                        OffshoreLeaksTestData.ExtensionJson(rows)));
            });
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        var options = OffshoreLeaksTestData.Options(
            maxRequestsPerQuery: 10,
            maxExtensionIds: 1);
        using var adapter = CreateAdapter(handler, time, lifetime, options);

        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => adapter.Value.SearchAsync(
                new ScreeningSourceQuery("Acme", "ACME"),
                TestContext.Current.CancellationToken));

        Assert.Equal(10, handler.Requests.Count);
    }

    [Fact]
    public async Task OneNamespaceFailureFailsTheWholeSource()
    {
        using var handler = new RecordingHttpMessageHandler(
            (request, _) => Task.FromResult(
                request.Uri.AbsolutePath.EndsWith(
                    "/panama-papers",
                    StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : OffshoreLeaksTestData.JsonResponse(
                        OffshoreLeaksTestData.ReconciliationJson())));
        using var lifetime = new TestHostApplicationLifetime();
        var time = new MutableTimeProvider(InitialTime);
        using var adapter = CreateAdapter(handler, time, lifetime);

        await Assert.ThrowsAsync<ScreeningSourceUnavailableException>(
            () => adapter.Value.SearchAsync(
                new ScreeningSourceQuery("Acme", "ACME"),
                TestContext.Current.CancellationToken));
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task ProviderScoreAndMatchDoNotAlterLocalExactScore()
    {
        var adapter = new FixedSourceAdapter(
            ScreeningSource.OffshoreLeaks,
            new ScreeningSourceCandidate("icij:1", "Acme", []));
        var orchestrator = new ScreeningOrchestrator(
            [adapter],
            new NoOpFailureReporter(),
            new ScreeningOptions
            {
                GlobalTimeoutSeconds = 40,
                Sources =
                {
                    [ScreeningSource.OffshoreLeaks] =
                        new ScreeningSourceOptions
                        {
                            MatchThreshold = 80,
                            TimeoutSeconds = 20,
                            ResultLimit = 100,
                        },
                },
            },
            TimeProvider.System);

        var execution = await orchestrator.ExecuteAsync(
            new ScreeningRequest(
                "Acme",
                [ScreeningSource.OffshoreLeaks]),
            TestContext.Current.CancellationToken);

        var source = Assert.Single(execution.Run!.Sources);
        var match = Assert.Single(source.Matches);
        Assert.Equal(100m, match.Score.OverallScore);
        Assert.True(match.Score.IsExactMatch);
    }

    [Fact]
    public async Task OffshoreFailureDoesNotDiscardAnotherSuccessfulSource()
    {
        var orchestrator = new ScreeningOrchestrator(
            [
                new ThrowingSourceAdapter(),
                new FixedSourceAdapter(
                    ScreeningSource.Ofac,
                    new ScreeningSourceCandidate("ofac:1", "Acme", [])),
            ],
            new NoOpFailureReporter(),
            new ScreeningOptions
            {
                GlobalTimeoutSeconds = 40,
                Sources =
                {
                    [ScreeningSource.OffshoreLeaks] =
                        new ScreeningSourceOptions
                        {
                            MatchThreshold = 80,
                            TimeoutSeconds = 20,
                            ResultLimit = 100,
                        },
                    [ScreeningSource.Ofac] =
                        new ScreeningSourceOptions
                        {
                            MatchThreshold = 80,
                            TimeoutSeconds = 35,
                            ResultLimit = 100,
                        },
                },
            },
            TimeProvider.System);

        var execution = await orchestrator.ExecuteAsync(
            new ScreeningRequest(
                "Acme",
                [ScreeningSource.OffshoreLeaks, ScreeningSource.Ofac]),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ScreeningRunStatus.PartiallyCompleted,
            execution.Run!.Status);
        Assert.Equal(
            ScreeningSourceStatus.Failed,
            execution.Run.Sources.Single(
                source => source.Source
                    == ScreeningSource.OffshoreLeaks).Status);
        Assert.Equal(
            ScreeningSourceStatus.Succeeded,
            execution.Run.Sources.Single(
                source => source.Source == ScreeningSource.Ofac).Status);
    }

    private static DisposableAdapter CreateAdapter(
        RecordingHttpMessageHandler handler,
        TimeProvider timeProvider,
        TestHostApplicationLifetime lifetime,
        OffshoreLeaksAdapterOptions? suppliedOptions = null)
    {
        var options = suppliedOptions ?? OffshoreLeaksTestData.Options();
        var optionsAccessor =
            Microsoft.Extensions.Options.Options.Create(options);
        var gate = new OffshoreLeaksHttpGate(optionsAccessor);
        var factory = new FixedHttpClientFactory(handler, options);
        var reconciliation = new IcijReconciliationClient(
            factory,
            gate,
            optionsAccessor,
            timeProvider,
            NullLogger<IcijReconciliationClient>.Instance);
        var extension = new IcijExtensionClient(
            factory,
            gate,
            optionsAccessor,
            timeProvider,
            NullLogger<IcijExtensionClient>.Instance);
        var cache = OffshoreLeaksTestData.Cache(
            options,
            timeProvider,
            lifetime);
        var adapter = new OffshoreLeaksScreeningSourceAdapter(
            reconciliation,
            extension,
            cache,
            optionsAccessor,
            timeProvider,
            NullLogger<OffshoreLeaksScreeningSourceAdapter>.Instance);
        return new DisposableAdapter(adapter, cache, gate);
    }

    private static RecordingHttpMessageHandler CompleteHandler(
        long id,
        string name) =>
        new((request, _) =>
        {
            var response = request.Method == HttpMethod.Post
                ? OffshoreLeaksTestData.JsonResponse(
                    OffshoreLeaksTestData.ReconciliationJson((id, name)),
                    HttpStatusCode.Created)
                : OffshoreLeaksTestData.JsonResponse(
                    OffshoreLeaksTestData.ExtensionJson((id, name)));
            return Task.FromResult(response);
        });

    private static IcijCandidate Candidate(long nodeId) =>
        new(nodeId, $"Entity {nodeId}", 1, false);

    private static Task<IReadOnlyList<IcijEnrichedEntity>> GetEntitiesAsync(
        OffshoreLeaksCache cache,
        IcijCandidate candidate,
        Func<
            IReadOnlyList<IcijCandidate>,
            OffshoreLeaksRequestBudget,
            CancellationToken,
            Task<IReadOnlyList<IcijEnrichedEntity>>> factory) =>
        cache.GetOrCreateEntitiesAsync(
            IcijNamespaces.Get(IcijNamespace.BahamasLeaks),
            [candidate],
            new OffshoreLeaksRequestBudget(10),
            factory,
            TestContext.Current.CancellationToken);

    private static void AssertField(
        ScreeningSourceCandidate candidate,
        string name,
        string expected) =>
        Assert.Equal(
            expected,
            Assert.Single(candidate.Fields, field => field.Name == name).Value);

    private sealed class DisposableAdapter(
        OffshoreLeaksScreeningSourceAdapter value,
        OffshoreLeaksCache cache,
        OffshoreLeaksHttpGate gate) : IDisposable
    {
        public OffshoreLeaksScreeningSourceAdapter Value => value;

        public void Dispose()
        {
            cache.Dispose();
            gate.Dispose();
        }
    }

    private sealed class FixedSourceAdapter(
        ScreeningSource source,
        params ScreeningSourceCandidate[] candidates)
        : IScreeningSourceAdapter
    {
        public ScreeningSource Source => source;

        public Task<IReadOnlyList<ScreeningSourceCandidate>> SearchAsync(
            ScreeningSourceQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>(candidates);
    }

    private sealed class ThrowingSourceAdapter : IScreeningSourceAdapter
    {
        public ScreeningSource Source => ScreeningSource.OffshoreLeaks;

        public Task<IReadOnlyList<ScreeningSourceCandidate>> SearchAsync(
            ScreeningSourceQuery query,
            CancellationToken cancellationToken) =>
            throw new OffshoreLeaksAdapterException("controlled");
    }

    private sealed class NoOpFailureReporter : IScreeningFailureReporter
    {
        public void ReportAdapterFailure(
            Guid runId,
            ScreeningSource source,
            Exception exception)
        {
        }
    }
}
