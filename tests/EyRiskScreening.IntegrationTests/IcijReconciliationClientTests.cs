using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;
using EyRiskScreening.IntegrationTests.Infrastructure;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class IcijReconciliationClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task AcceptsDocumentedSuccessStatuses(HttpStatusCode status)
    {
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(
                OffshoreLeaksTestData.ReconciliationJson((101, "Acme")),
                status));
        var client = OffshoreLeaksTestData.ReconciliationClient(handler);

        var result = await SearchAsync(client, "Acme");

        var candidate = Assert.Single(result);
        Assert.Equal(101, candidate.NodeId);
        Assert.Equal("Acme", candidate.Name);
        Assert.Equal(42.5, candidate.ProviderScore);
        Assert.False(candidate.ProviderMatch);
    }

    [Fact]
    public async Task SendsExactNamespacedEntityQueryAndSafeHeaders()
    {
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(
                OffshoreLeaksTestData.ReconciliationJson()));
        var client = OffshoreLeaksTestData.ReconciliationClient(handler);

        _ = await SearchAsync(client, "Ácme Holdings");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "/api/v1/reconcile/bahamas-leaks",
            request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(
            "Ácme Holdings",
            body.RootElement.GetProperty("query").GetString());
        Assert.Equal(
            "Entity",
            body.RootElement.GetProperty("type").GetString());
        Assert.Equal(["application/json"], request.Accept);
        Assert.Equal(["EY-Risk-Screening-Tests/1.0"], request.UserAgent);
        Assert.False(request.HasAuthorization);
        Assert.False(request.HasProxyAuthorization);
        Assert.False(request.HasCookie);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(25)]
    public async Task AcceptsCandidateCountsWithinLimit(int count)
    {
        var candidates = Enumerable.Range(1, count)
            .Select(index => ((long)index, $"Entity {index}"))
            .ToArray();
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(
                OffshoreLeaksTestData.ReconciliationJson(candidates)));

        var result = await SearchAsync(
            OffshoreLeaksTestData.ReconciliationClient(handler),
            "Entity");

        Assert.Equal(count, result.Count);
    }

    [Fact]
    public async Task RejectsTwentySixCandidates()
    {
        var candidates = Enumerable.Range(1, 26)
            .Select(index => ((long)index, $"Entity {index}"))
            .ToArray();
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(
                OffshoreLeaksTestData.ReconciliationJson(candidates)));

        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => SearchAsync(
                OffshoreLeaksTestData.ReconciliationClient(handler),
                "Entity"));
    }

    public static TheoryData<string> InvalidCandidateDocuments() =>
        new()
        {
            """{"result":[{"id":"0","name":"Acme","types":["Entity"],"score":1}]}""",
            """{"result":[{"id":"abc","name":"Acme","types":["Entity"],"score":1}]}""",
            """{"result":[{"id":"1","name":"","types":["Entity"],"score":1}]}""",
            """{"result":[{"id":"1","name":"Acme","score":1}]}""",
            """{"result":[{"id":"1","name":"Acme","types":["Person"],"score":1}]}""",
            """{"result":[{"id":"1","name":"Acme","types":["Entity"],"score":"1"}]}""",
            """{"result":[{"id":"1","name":"Acme","types":["Entity"],"score":1,"match":"true"}]}""",
            """{"result":[{"id":"1","name":"Acme\n","types":["Entity"],"score":1}]}""",
            """{"wrong":[]}""",
            """[]""",
        };

    [Theory]
    [MemberData(nameof(InvalidCandidateDocuments))]
    public async Task RejectsInvalidCandidateContracts(string json)
    {
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(json));

        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => SearchAsync(
                OffshoreLeaksTestData.ReconciliationClient(handler),
                "Acme"));
    }

    [Fact]
    public async Task DeduplicatesCompatibleIdsButPreservesDistinctIds()
    {
        const string json = """
            {
              "result": [
                {"id":"1","name":"Acme, Inc.","types":["Entity"],"score":1,"match":true},
                {"id":"1","name":"ACME INC","types":["Entity"],"score":99,"match":false},
                {"id":"2","name":"Acme, Inc.","types":["Entity"],"score":50}
              ]
            }
            """;
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(json));

        var result = await SearchAsync(
            OffshoreLeaksTestData.ReconciliationClient(handler),
            "Acme");

        Assert.Equal([1L, 2L], result.Select(item => item.NodeId));
        Assert.Equal("Acme, Inc.", result[0].Name);
    }

    [Fact]
    public async Task RejectsConflictingDuplicateId()
    {
        const string json = """
            {"result":[
              {"id":"1","name":"Acme","types":["Entity"],"score":1},
              {"id":"1","name":"Other","types":["Entity"],"score":1}
            ]}
            """;
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(json));

        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => SearchAsync(
                OffshoreLeaksTestData.ReconciliationClient(handler),
                "Acme"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, typeof(OffshoreLeaksAdapterException))]
    [InlineData(HttpStatusCode.NotFound, typeof(OffshoreLeaksAdapterException))]
    [InlineData(HttpStatusCode.RequestTimeout, typeof(ScreeningSourceTimedOutException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(ScreeningSourceUnavailableException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(ScreeningSourceUnavailableException))]
    [InlineData(HttpStatusCode.Redirect, typeof(OffshoreLeaksAdapterException))]
    public async Task MapsHttpFailuresWithoutRetry(
        HttpStatusCode status,
        Type exceptionType)
    {
        using var handler = Handler(_ => new HttpResponseMessage(status));
        var exception = await Record.ExceptionAsync(
            () => SearchAsync(
                OffshoreLeaksTestData.ReconciliationClient(handler),
                "Acme"));

        Assert.IsType(exceptionType, exception);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MapsTransportFailureToUnavailableWithoutRetry()
    {
        using var handler = new RecordingHttpMessageHandler(
            (_, _) => throw new HttpRequestException(
                "Controlled transport failure."));

        await Assert.ThrowsAsync<ScreeningSourceUnavailableException>(
            () => SearchAsync(
                OffshoreLeaksTestData.ReconciliationClient(handler),
                "Acme"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ParsesRetryAfterWithoutRetrying()
    {
        using var handler = Handler(_ =>
        {
            var response = new HttpResponseMessage(
                HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(
                TimeSpan.FromSeconds(12));
            return response;
        });

        await Assert.ThrowsAsync<ScreeningSourceUnavailableException>(
            () => SearchAsync(
                OffshoreLeaksTestData.ReconciliationClient(handler),
                "Acme"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RejectsInvalidJsonContentTypeDepthAndDecodedSize()
    {
        await AssertFailedAsync("{", "application/json");
        await AssertFailedAsync(
            OffshoreLeaksTestData.ReconciliationJson(),
            "text/html");
        await AssertFailedAsync(
            """{"result":[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]}""",
            "application/json");

        var options = OffshoreLeaksTestData.Options(maxQueryBytes: 1024);
        var oversized = JsonSerializer.Serialize(new
        {
            result = Array.Empty<object>(),
            padding = new string('x', 2000),
        });
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(oversized));
        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => SearchAsync(
                OffshoreLeaksTestData.ReconciliationClient(handler, options),
                "Acme"));
    }

    [Fact]
    public async Task PropagatesCallerCancellation()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHttpMessageHandler(
            async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            });
        using var cancellation = new CancellationTokenSource();
        var task = SearchAsyncWithToken(
            OffshoreLeaksTestData.ReconciliationClient(handler),
            "Acme",
            cancellation.Token);
        await started.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static RecordingHttpMessageHandler Handler(
        Func<RecordedHttpRequest, HttpResponseMessage> responder) =>
        new((request, _) => Task.FromResult(responder(request)));

    private static Task<IReadOnlyList<IcijCandidate>> SearchAsync(
        IcijReconciliationClient client,
        string name) =>
        client.SearchAsync(
            IcijNamespaces.Get(IcijNamespace.BahamasLeaks),
            name,
            new OffshoreLeaksRequestBudget(10),
            TestContext.Current.CancellationToken);

    private static Task<IReadOnlyList<IcijCandidate>> SearchAsyncWithToken(
        IcijReconciliationClient client,
        string name,
        CancellationToken cancellationToken) =>
        client.SearchAsync(
            IcijNamespaces.Get(IcijNamespace.BahamasLeaks),
            name,
            new OffshoreLeaksRequestBudget(10),
            cancellationToken);

    private static async Task AssertFailedAsync(
        string content,
        string mediaType)
    {
        using var handler = Handler(_ =>
            OffshoreLeaksTestData.JsonResponse(
                content,
                mediaType: mediaType));
        await Assert.ThrowsAsync<OffshoreLeaksAdapterException>(
            () => SearchAsync(
                OffshoreLeaksTestData.ReconciliationClient(handler),
                "Acme"));
    }
}
