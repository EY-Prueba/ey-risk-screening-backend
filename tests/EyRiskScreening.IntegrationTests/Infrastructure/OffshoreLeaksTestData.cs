using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

internal static class OffshoreLeaksTestData
{
    private static readonly string[] EntityTypes = ["Entity"];

    public const string EntitySchema =
        "https://offshoreleaks.icij.org/schema/oldb/entity";

    public static OffshoreLeaksAdapterOptions Options(
        int queryTtlMinutes = 30,
        int queryMaxEntries = 500,
        int entityTtlHours = 6,
        int entityMaxEntries = 5000,
        int maxConcurrentRequests = 2,
        int maxRequestsPerQuery = 10,
        int maxExtensionIds = 25,
        int maxQueryBytes = 524288,
        int maxExtensionBytes = 1048576) =>
        new()
        {
            BaseUrl = "http://127.0.0.1:12345/",
            QueryCacheTtlMinutes = queryTtlMinutes,
            QueryCacheMaxEntries = queryMaxEntries,
            EntityCacheTtlHours = entityTtlHours,
            EntityCacheMaxEntries = entityMaxEntries,
            MaxCandidatesPerNamespace = 25,
            MaxCandidatesBeforeDeduplication = 125,
            MaxExtensionIds = maxExtensionIds,
            MaxConcurrentRequests = maxConcurrentRequests,
            MaxRequestsPerQuery = maxRequestsPerQuery,
            MaxQueryResponseBytes = maxQueryBytes,
            MaxExtensionResponseBytes = maxExtensionBytes,
            MaxJsonDepth = 16,
            UserAgent = "EY-Risk-Screening-Tests/1.0",
        };

    public static IcijReconciliationClient ReconciliationClient(
        RecordingHttpMessageHandler handler,
        OffshoreLeaksAdapterOptions? options = null)
    {
        var current = options ?? Options();
        return new IcijReconciliationClient(
            new FixedHttpClientFactory(handler, current),
            new OffshoreLeaksHttpGate(
                Microsoft.Extensions.Options.Options.Create(current)),
            Microsoft.Extensions.Options.Options.Create(current),
            TimeProvider.System,
            NullLogger<IcijReconciliationClient>.Instance);
    }

    public static IcijExtensionClient ExtensionClient(
        RecordingHttpMessageHandler handler,
        OffshoreLeaksAdapterOptions? options = null)
    {
        var current = options ?? Options();
        return new IcijExtensionClient(
            new FixedHttpClientFactory(handler, current),
            new OffshoreLeaksHttpGate(
                Microsoft.Extensions.Options.Options.Create(current)),
            Microsoft.Extensions.Options.Options.Create(current),
            TimeProvider.System,
            NullLogger<IcijExtensionClient>.Instance);
    }

    public static OffshoreLeaksCache Cache(
        OffshoreLeaksAdapterOptions options,
        TimeProvider timeProvider,
        TestHostApplicationLifetime lifetime) =>
        new(
            Microsoft.Extensions.Options.Options.Create(options),
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
            timeProvider,
            lifetime);

    public static string ReconciliationJson(
        params (long Id, string Name)[] candidates)
    {
        var result = candidates.Select(candidate => new Dictionary<string, object?>
        {
            ["id"] = candidate.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["name"] = candidate.Name,
            ["types"] = EntityTypes,
            ["score"] = 42.5,
            ["match"] = false,
            ["description"] = "not persisted",
        });
        return JsonSerializer.Serialize(new { result });
    }

    public static string ExtensionJson(
        params (long Id, string Name)[] rows)
    {
        var properties = new[]
        {
            "jurisdiction",
            "jurisdiction_description",
            "country_codes",
            "countries",
            "sourceID",
            "name",
            "icij_id",
            "schema",
        };
        var mappedRows = rows.ToDictionary(
            row => row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            row => (object)new Dictionary<string, object?>
            {
                ["jurisdiction"] = Values("BVI"),
                ["jurisdiction_description"] = Values("British Virgin Islands"),
                ["country_codes"] = Values("GB", "US"),
                ["countries"] = Values("United Kingdom", "United States"),
                ["sourceID"] = Values("not-used"),
                ["name"] = Values(row.Name),
                ["icij_id"] = Values($"ICIJ-{row.Id}"),
                ["schema"] = Values(EntitySchema),
            });
        return JsonSerializer.Serialize(new
        {
            meta = properties.Select(property => new { id = property }),
            rows = mappedRows,
        });
    }

    public static object[] Values(params string[] values) =>
        values.Select(value => (object)new { str = value }).ToArray();

    public static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "application/json") =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, mediaType),
        };
}

internal sealed record RecordedHttpRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    string[] Accept,
    string[] UserAgent,
    bool HasAuthorization,
    bool HasProxyAuthorization,
    bool HasCookie);

internal sealed class RecordingHttpMessageHandler(
    Func<RecordedHttpRequest, CancellationToken, Task<HttpResponseMessage>>
        responder) : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedHttpRequest> _requests = [];
    private int _active;
    private int _maximumActive;

    public IReadOnlyList<RecordedHttpRequest> Requests => _requests.ToArray();

    public int MaximumActive => Volatile.Read(ref _maximumActive);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        UpdateMaximum(active);
        try
        {
            var body = request.Content is null
                ? null
                : await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            var recorded = new RecordedHttpRequest(
                request.Method,
                request.RequestUri!,
                body,
                request.Headers.Accept.Select(value => value.MediaType!).ToArray(),
                request.Headers.UserAgent.Select(value => value.ToString()).ToArray(),
                request.Headers.Authorization is not null,
                request.Headers.ProxyAuthorization is not null,
                request.Headers.Contains("Cookie"));
            _requests.Enqueue(recorded);
            return await responder(recorded, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _active);
        }
    }

    private void UpdateMaximum(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maximumActive);
            if (current >= active
                || Interlocked.CompareExchange(
                    ref _maximumActive,
                    active,
                    current) == current)
            {
                return;
            }
        }
    }
}

internal sealed class FixedHttpClientFactory(
    HttpMessageHandler handler,
    OffshoreLeaksAdapterOptions options) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        Assert.Equal(IcijReconciliationClient.ClientName, name);
        var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        return client;
    }
}
