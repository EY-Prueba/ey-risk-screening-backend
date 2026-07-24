using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.UnitTests.Fakes;
using EyRiskScreening.UnitTests.Infrastructure;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class ScreeningOrchestratorTests
{
    private static readonly DateTimeOffset FixedUtcNow =
        new(2026, 7, 22, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OneSuccessfulSourceCanReturnZeroHits()
    {
        var adapter = CreateAdapter(ScreeningSource.Ofac, []);
        var orchestrator = CreateOrchestrator([adapter]);

        var execution = await orchestrator.ExecuteAsync(
            new ScreeningRequest("  Ácme Corporation  ", [ScreeningSource.Ofac]),
            TestContext.Current.CancellationToken);

        Assert.True(execution.IsValid);
        var run = Assert.IsType<ScreeningRunResult>(execution.Run);
        Assert.Equal(ScreeningRunStatus.Completed, run.Status);
        Assert.Equal("Ácme Corporation", run.EntityName);
        Assert.Equal("ACME CORPORATION", run.NormalizedEntityName);
        Assert.Equal(0, run.TotalHits);
        Assert.Equal(0, run.TotalReturnedResults);
        Assert.Equal(ScreeningSourceStatus.Succeeded, Assert.Single(run.Sources).Status);
    }

    [Fact]
    public async Task RunAndSourceTimesUseTheControlledTimeProviderExactly()
    {
        var timeProvider = new ManualTimeProvider(FixedUtcNow);
        var elapsed = TimeSpan.FromSeconds(3);
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) =>
            {
                timeProvider.Advance(elapsed);
                return Task.FromResult<IReadOnlyList<ScreeningSourceCandidate>>([]);
            });
        var orchestrator = CreateOrchestrator([adapter], timeProvider: timeProvider);

        var execution = await orchestrator.ExecuteAsync(
            new ScreeningRequest("Acme Corporation", [ScreeningSource.Ofac]),
            TestContext.Current.CancellationToken);

        var run = Assert.IsType<ScreeningRunResult>(execution.Run);
        Assert.NotEqual(Guid.Empty, run.RunId);
        Assert.Equal(FixedUtcNow, run.RequestedAtUtc);
        Assert.Equal(FixedUtcNow.Add(elapsed), run.CompletedAtUtc);
        Assert.True(run.CompletedAtUtc >= run.RequestedAtUtc);
        Assert.Equal(elapsed, run.TotalDuration);
        Assert.Equal(elapsed, Assert.Single(run.Sources).Duration);
    }

    [Fact]
    public async Task ThreeSourcesStartConcurrentlyAndReturnInDeterministicOrder()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var adapters = Enum.GetValues<ScreeningSource>()
            .Reverse()
            .Select(source => new FakeScreeningSourceAdapter(source, async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref startedCount) == 3)
                {
                    allStarted.SetResult();
                }

                await release.Task.WaitAsync(cancellationToken);
                return [CreateCandidate($"{source}-1", "Acme Corporation")];
            }))
            .ToArray();
        var orchestrator = CreateOrchestrator(adapters);

        var executionTask = orchestrator.ExecuteAsync(
            new ScreeningRequest(
                "Acme Corporation",
                [ScreeningSource.Ofac, ScreeningSource.OffshoreLeaks, ScreeningSource.WorldBank]),
            TestContext.Current.CancellationToken);
        await allStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        var execution = await executionTask;

        var run = Assert.IsType<ScreeningRunResult>(execution.Run);
        Assert.Equal(ScreeningRunStatus.Completed, run.Status);
        Assert.Equal(Enum.GetValues<ScreeningSource>(), run.Sources.Select(result => result.Source));
        Assert.All(adapters, adapter => Assert.Equal(1, adapter.CallCount));
    }

    [Fact]
    public async Task FailedSourceAndTwoSuccessesProducePartialResult()
    {
        var reporter = new FakeScreeningFailureReporter();
        var internalException = new InvalidOperationException("private adapter response");
        var adapters = new IScreeningSourceAdapter[]
        {
            CreateAdapter(ScreeningSource.OffshoreLeaks, [CreateCandidate("1", "Acme Corporation")]),
            new FakeScreeningSourceAdapter(
                ScreeningSource.WorldBank,
                (_, _) => Task.FromException<IReadOnlyList<ScreeningSourceCandidate>>(internalException)),
            CreateAdapter(ScreeningSource.Ofac, []),
        };
        var orchestrator = CreateOrchestrator(adapters, reporter);

        var execution = await orchestrator.ExecuteAsync(
            new ScreeningRequest("Acme Corporation", Enum.GetValues<ScreeningSource>()),
            TestContext.Current.CancellationToken);

        var run = Assert.IsType<ScreeningRunResult>(execution.Run);
        Assert.Equal(ScreeningRunStatus.PartiallyCompleted, run.Status);
        Assert.Equal(2, run.Sources.Count(source => source.Status == ScreeningSourceStatus.Succeeded));
        var failed = Assert.Single(run.Sources, source => source.Status == ScreeningSourceStatus.Failed);
        Assert.Equal(ScreeningSourceErrorCode.SourceFailed, failed.Error?.Code);
        Assert.DoesNotContain("private", failed.Error?.Message ?? string.Empty);
        Assert.Same(internalException, reporter.Exception);
        Assert.Equal(ScreeningSource.WorldBank, reporter.Source);
    }

    [Fact]
    public async Task UnregisteredSourceIsUnavailableWithoutPreventingStartup()
    {
        var orchestrator = CreateOrchestrator([]);

        var execution = await orchestrator.ExecuteAsync(
            new ScreeningRequest("Acme Corporation", [ScreeningSource.WorldBank]),
            TestContext.Current.CancellationToken);

        var run = Assert.IsType<ScreeningRunResult>(execution.Run);
        Assert.Equal(ScreeningRunStatus.Failed, run.Status);
        var source = Assert.Single(run.Sources);
        Assert.Equal(ScreeningSourceStatus.Unavailable, source.Status);
        Assert.Equal(ScreeningSourceErrorCode.SourceUnavailable, source.Error?.Code);
    }

    [Fact]
    public async Task IndividualTimeoutIsClassifiedSeparately()
    {
        var timeProvider = new ManualTimeProvider(FixedUtcNow);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeScreeningSourceAdapter(ScreeningSource.Ofac, async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });
        var options = CreateOptions(sourceTimeoutSeconds: 15, globalTimeoutSeconds: 40);
        var orchestrator = CreateOrchestrator([adapter], timeProvider: timeProvider, options: options);

        var executionTask = orchestrator.ExecuteAsync(
            new ScreeningRequest("Acme Corporation", [ScreeningSource.Ofac]),
            CancellationToken.None);
        await started.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        var execution = await executionTask;

        var source = Assert.Single(Assert.IsType<ScreeningRunResult>(execution.Run).Sources);
        Assert.Equal(ScreeningSourceStatus.TimedOut, source.Status);
        Assert.Equal(ScreeningSourceErrorCode.SourceTimedOut, source.Error?.Code);
    }

    [Fact]
    public async Task GlobalTimeoutClassifiesAllIncompleteSources()
    {
        var timeProvider = new ManualTimeProvider(FixedUtcNow);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var adapters = new[] { ScreeningSource.OffshoreLeaks, ScreeningSource.WorldBank }
            .Select(source => new FakeScreeningSourceAdapter(source, async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    allStarted.SetResult();
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }))
            .ToArray();
        var options = CreateOptions(sourceTimeoutSeconds: 30, globalTimeoutSeconds: 10);
        var orchestrator = CreateOrchestrator(adapters, timeProvider: timeProvider, options: options);

        var executionTask = orchestrator.ExecuteAsync(
            new ScreeningRequest(
                "Acme Corporation",
                [ScreeningSource.OffshoreLeaks, ScreeningSource.WorldBank]),
            CancellationToken.None);
        await allStarted.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        var execution = await executionTask;

        var run = Assert.IsType<ScreeningRunResult>(execution.Run);
        Assert.Equal(ScreeningRunStatus.Failed, run.Status);
        Assert.All(run.Sources, source =>
        {
            Assert.Equal(ScreeningSourceStatus.TimedOut, source.Status);
            Assert.Equal(ScreeningSourceErrorCode.GlobalTimeout, source.Error?.Code);
        });
    }

    [Fact]
    public async Task ClientCancellationPropagatesAndAllAdaptersObserveIt()
    {
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var cancellationObserved = 0;
        var adapters = new[] { ScreeningSource.OffshoreLeaks, ScreeningSource.WorldBank }
            .Select(source => new FakeScreeningSourceAdapter(source, async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    allStarted.SetResult();
                }

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return [];
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref cancellationObserved);
                    }
                }
            }))
            .ToArray();
        var orchestrator = CreateOrchestrator(adapters);
        using var cancellation = new CancellationTokenSource();

        var executionTask = orchestrator.ExecuteAsync(
            new ScreeningRequest(
                "Acme Corporation",
                [ScreeningSource.OffshoreLeaks, ScreeningSource.WorldBank]),
            cancellation.Token);
        await allStarted.Task;
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executionTask);
        Assert.Equal(2, cancellationObserved);
    }

    [Fact]
    public async Task HitsAreCountedBeforeLimitAndMatchesAreDeterministicallyOrdered()
    {
        var adapter = CreateAdapter(
            ScreeningSource.Ofac,
            [
                CreateCandidate("C", "Acme Corporation", [new ScreeningSourceField("z", "2")]),
                CreateCandidate("A", "Acme Corporation", [new ScreeningSourceField("a", "1")]),
                CreateCandidate("B", "Acme Corporation"),
            ]);
        var options = CreateOptions(resultLimit: 2);
        var orchestrator = CreateOrchestrator([adapter], options: options);

        var execution = await orchestrator.ExecuteAsync(
            new ScreeningRequest("Acme Corporation", [ScreeningSource.Ofac]),
            TestContext.Current.CancellationToken);

        var source = Assert.Single(Assert.IsType<ScreeningRunResult>(execution.Run).Sources);
        Assert.Equal(3, source.Hits);
        Assert.Equal(2, source.ReturnedResults);
        Assert.Equal(["A", "B"], source.Matches.Select(match => match.ReferenceId));
    }

    [Fact]
    public async Task ThresholdIsConfigurableAndAppliedOutsideScorer()
    {
        var adapter = CreateAdapter(
            ScreeningSource.Ofac,
            [CreateCandidate("1", "Group Acme")]);
        var accepted = CreateOrchestrator(
            [adapter],
            options: CreateOptions(matchThreshold: 60));
        var rejected = CreateOrchestrator(
            [adapter],
            options: CreateOptions(matchThreshold: 61));
        var request = new ScreeningRequest("Acme Group", [ScreeningSource.Ofac]);

        var acceptedResult = await accepted.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);
        var rejectedResult = await rejected.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        var acceptedSource = Assert.Single(
            Assert.IsType<ScreeningRunResult>(acceptedResult.Run).Sources);
        var rejectedSource = Assert.Single(
            Assert.IsType<ScreeningRunResult>(rejectedResult.Run).Sources);
        Assert.Equal(60, acceptedSource.MatchThreshold);
        Assert.Equal(61, rejectedSource.MatchThreshold);
        Assert.Equal(1, acceptedSource.Hits);
        Assert.Equal(0, rejectedSource.Hits);
    }

    [Fact]
    public async Task BestAliasProducesOneHitForTheCandidateUid()
    {
        var candidate = new ScreeningSourceCandidate(
            "ofac-1001",
            "Unrelated Primary Name",
            [new ScreeningSourceField("List", "SDN")],
            [
                new ScreeningSourceAlternativeName(
                    "Acme Corporation",
                    [
                        new ScreeningSourceField("AliasType", "a.k.a."),
                        new ScreeningSourceField("AliasQuality", "strong"),
                    ]),
                new ScreeningSourceAlternativeName("Acme Corp", []),
            ]);
        var orchestrator = CreateOrchestrator(
            [CreateAdapter(ScreeningSource.Ofac, [candidate])]);

        var execution = await orchestrator.ExecuteAsync(
            new ScreeningRequest("Acme Corporation", [ScreeningSource.Ofac]),
            TestContext.Current.CancellationToken);

        var source = Assert.Single(Assert.IsType<ScreeningRunResult>(execution.Run).Sources);
        var match = Assert.Single(source.Matches);
        Assert.Equal(1, source.Hits);
        Assert.Equal("ofac-1001", match.ReferenceId);
        Assert.Equal("Acme Corporation", match.Name);
        Assert.True(match.Score.IsExactMatch);
        Assert.Contains(
            match.Fields,
            field => field.Name == "AliasType" && field.Value == "a.k.a.");
    }

    [Fact]
    public async Task CancellationIsCheckedWhileTraversingLargeAliasSets()
    {
        using var cancellation = new CancellationTokenSource();
        var alternativeNames = new CancellingAlternativeNames(
            cancellation,
            cancelAtIndex: 25,
            count: 500);
        var candidate = new ScreeningSourceCandidate(
            "ofac-1001",
            "Unrelated Primary Name",
            [],
            alternativeNames);
        var orchestrator = CreateOrchestrator(
            [CreateAdapter(ScreeningSource.Ofac, [candidate])]);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.ExecuteAsync(
                new ScreeningRequest("Acme Corporation", [ScreeningSource.Ofac]),
                cancellation.Token));

        Assert.InRange(alternativeNames.EnumeratedCount, 25, 26);
    }

    [Theory]
    [InlineData(true, ScreeningSourceStatus.TimedOut)]
    [InlineData(false, ScreeningSourceStatus.Unavailable)]
    public async Task ExpectedAdapterFailuresMapToTypedSourceStatuses(
        bool timedOut,
        ScreeningSourceStatus expectedStatus)
    {
        Exception exception = timedOut
            ? new ScreeningSourceTimedOutException("private timeout")
            : new ScreeningSourceUnavailableException("private unavailable");
        var adapter = new FakeScreeningSourceAdapter(
            ScreeningSource.Ofac,
            (_, _) => Task.FromException<IReadOnlyList<ScreeningSourceCandidate>>(
                exception));
        var reporter = new FakeScreeningFailureReporter();
        var orchestrator = CreateOrchestrator([adapter], reporter);

        var execution = await orchestrator.ExecuteAsync(
            new ScreeningRequest("Acme Corporation", [ScreeningSource.Ofac]),
            TestContext.Current.CancellationToken);

        var source = Assert.Single(Assert.IsType<ScreeningRunResult>(execution.Run).Sources);
        Assert.Equal(expectedStatus, source.Status);
        Assert.Null(reporter.Exception);
    }

    [Fact]
    public async Task InvalidRequestDoesNotInvokeAdapters()
    {
        var adapter = CreateAdapter(ScreeningSource.Ofac, []);
        var orchestrator = CreateOrchestrator([adapter]);

        var execution = await orchestrator.ExecuteAsync(
            new ScreeningRequest(" ", [ScreeningSource.Ofac]),
            TestContext.Current.CancellationToken);

        Assert.False(execution.IsValid);
        Assert.Null(execution.Run);
        Assert.Equal(0, adapter.CallCount);
    }

    private static FakeScreeningSourceAdapter CreateAdapter(
        ScreeningSource source,
        IReadOnlyList<ScreeningSourceCandidate> candidates) =>
        new(source, (_, _) => Task.FromResult(candidates));

    private static ScreeningSourceCandidate CreateCandidate(
        string referenceId,
        string name,
        IReadOnlyList<ScreeningSourceField>? fields = null) =>
        new(referenceId, name, fields ?? []);

    private static ScreeningOrchestrator CreateOrchestrator(
        IEnumerable<IScreeningSourceAdapter> adapters,
        FakeScreeningFailureReporter? reporter = null,
        ManualTimeProvider? timeProvider = null,
        ScreeningOptions? options = null) =>
        new(
            adapters,
            reporter ?? new FakeScreeningFailureReporter(),
            options ?? CreateOptions(),
            timeProvider ?? new ManualTimeProvider(FixedUtcNow));

    private static ScreeningOptions CreateOptions(
        int matchThreshold = 80,
        int sourceTimeoutSeconds = 20,
        int globalTimeoutSeconds = 40,
        int resultLimit = 100) =>
        new()
        {
            GlobalTimeoutSeconds = globalTimeoutSeconds,
            Sources = Enum.GetValues<ScreeningSource>().ToDictionary(
                source => source,
                _ => new ScreeningSourceOptions
                {
                    MatchThreshold = matchThreshold,
                    TimeoutSeconds = sourceTimeoutSeconds,
                    ResultLimit = resultLimit,
                }),
        };

    private sealed class CancellingAlternativeNames(
        CancellationTokenSource cancellation,
        int cancelAtIndex,
        int count) : IReadOnlyList<ScreeningSourceAlternativeName>
    {
        public int Count => count;

        public int EnumeratedCount { get; private set; }

        public ScreeningSourceAlternativeName this[int index] =>
            new($"Alias {index}", []);

        public IEnumerator<ScreeningSourceAlternativeName> GetEnumerator()
        {
            for (var index = 0; index < count; index++)
            {
                EnumeratedCount++;
                if (index == cancelAtIndex)
                {
                    cancellation.Cancel();
                }

                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
