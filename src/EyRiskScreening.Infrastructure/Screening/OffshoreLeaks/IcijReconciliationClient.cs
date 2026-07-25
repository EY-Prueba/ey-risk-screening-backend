using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal sealed partial class IcijReconciliationClient(
    IHttpClientFactory httpClientFactory,
    OffshoreLeaksHttpGate httpGate,
    IOptions<OffshoreLeaksAdapterOptions> optionsAccessor,
    TimeProvider timeProvider,
    ILogger<IcijReconciliationClient> logger)
    : IIcijReconciliationClient
{
    public const string ClientName = "OffshoreLeaks";
    private const string EntitySchema =
        "https://offshoreleaks.icij.org/schema/oldb/entity";
    private static readonly IReadOnlySet<HttpStatusCode> AcceptedStatuses =
        new HashSet<HttpStatusCode>
        {
            HttpStatusCode.OK,
            HttpStatusCode.Created,
        };
    private readonly OffshoreLeaksAdapterOptions _options = optionsAccessor.Value;

    public Task<IReadOnlyList<IcijCandidate>> SearchAsync(
        IcijNamespaceDefinition @namespace,
        string entityName,
        OffshoreLeaksRequestBudget requestBudget,
        CancellationToken cancellationToken) =>
        httpGate.ExecuteAsync(
            token => SearchCoreAsync(
                @namespace,
                entityName,
                requestBudget,
                token),
            cancellationToken);

    private async Task<IReadOnlyList<IcijCandidate>> SearchCoreAsync(
        IcijNamespaceDefinition @namespace,
        string entityName,
        OffshoreLeaksRequestBudget requestBudget,
        CancellationToken cancellationToken)
    {
        requestBudget.Consume();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            @namespace.ReconcilePath);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new ByteArrayContent(CreateQueryBody(entityName));
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };

        try
        {
            var client = httpClientFactory.CreateClient(ClientName);
            using var response = await client
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode
                == StatusCodes.Status429TooManyRequests)
            {
                LogRateLimited(
                    logger,
                    OffshoreLeaksHttpResponseReader
                        .ParseRetryAfter(
                            response,
                            timeProvider.GetUtcNow())?
                        .TotalSeconds);
            }

            using var document = await OffshoreLeaksHttpResponseReader
                .ReadJsonAsync(
                    response,
                    AcceptedStatuses,
                    _options.MaxQueryResponseBytes,
                    _options.MaxJsonDepth,
                    cancellationToken)
                .ConfigureAwait(false);
            return ParseCandidates(document.RootElement);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new ScreeningSourceUnavailableException(
                "The ICIJ reconciliation service is unavailable.",
                exception);
        }
    }

    private IcijCandidate[] ParseCandidates(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array)
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ reconciliation response has an incompatible contract.");
        }

        var count = result.GetArrayLength();
        if (count > _options.MaxCandidatesPerNamespace)
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ reconciliation response exceeded its candidate limit.");
        }

        var candidates = new Dictionary<long, IcijCandidate>();
        foreach (var item in result.EnumerateArray())
        {
            var candidate = ParseCandidate(item);
            if (candidates.TryGetValue(candidate.NodeId, out var existing))
            {
                if (!NamesAreCompatible(existing.Name, candidate.Name))
                {
                    throw new OffshoreLeaksAdapterException(
                        "The ICIJ reconciliation response contains conflicting duplicate identifiers.");
                }

                continue;
            }

            candidates.Add(candidate.NodeId, candidate);
        }

        return candidates.Values
            .OrderBy(candidate => candidate.NodeId)
            .ToArray();
    }

    private static IcijCandidate ParseCandidate(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ candidate has an incompatible contract.");
        }

        var nodeId = ParseNodeId(item);
        var name = RequiredString(item, "name", ScreeningHistoryLimits.MatchNameRunes);
        if (!item.TryGetProperty("types", out var types)
            || types.ValueKind != JsonValueKind.Array
            || !types.EnumerateArray().Any(IsEntityType))
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ candidate is not an Entity.");
        }

        if (!item.TryGetProperty("score", out var scoreElement)
            || scoreElement.ValueKind != JsonValueKind.Number
            || !scoreElement.TryGetDouble(out var providerScore)
            || !double.IsFinite(providerScore))
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ candidate has an invalid diagnostic score.");
        }

        bool? providerMatch = null;
        if (item.TryGetProperty("match", out var matchElement))
        {
            if (matchElement.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False))
            {
                throw new OffshoreLeaksAdapterException(
                    "An ICIJ candidate has an invalid diagnostic match flag.");
            }

            providerMatch = matchElement.GetBoolean();
        }

        if (item.TryGetProperty("description", out var description)
            && description.ValueKind is not (
                JsonValueKind.String or JsonValueKind.Null))
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ candidate has an invalid description.");
        }

        return new IcijCandidate(nodeId, name, providerScore, providerMatch);
    }

    private static long ParseNodeId(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var id))
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ candidate is missing its identifier.");
        }

        var text = id.ValueKind switch
        {
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.String => id.GetString(),
            _ => null,
        };
        if (string.IsNullOrEmpty(text)
            || text.Length > 20
            || !long.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
            || value <= 0)
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ candidate has an invalid identifier.");
        }

        return value;
    }

    private static string RequiredString(
        JsonElement item,
        string propertyName,
        int maximumRunes)
    {
        if (!item.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ candidate is missing required text.");
        }

        var value = property.GetString()!;
        try
        {
            ScreeningHistoryGuard.RequiredText(
                value,
                maximumRunes,
                propertyName);
        }
        catch (ScreeningHistoryValidationException exception)
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ candidate exceeds the screening history limits.",
                exception);
        }

        return value.Normalize(NormalizationForm.FormC);
    }

    private static bool IsEntityType(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var type = value.GetString();
            return string.Equals(type, "Entity", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, EntitySchema, StringComparison.Ordinal);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return (value.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(
                    id.GetString(),
                    EntitySchema,
                    StringComparison.Ordinal))
            || (value.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(
                    name.GetString(),
                    "Entity",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool NamesAreCompatible(string first, string second) =>
        string.Equals(
            EntityNameNormalizer.Normalize(first).Value,
            EntityNameNormalizer.Normalize(second).Value,
            StringComparison.Ordinal);

    private static byte[] CreateQueryBody(string entityName)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("query", entityName);
            writer.WriteString("type", "Entity");
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Debug,
        Message = "ICIJ reconciliation was rate limited; Retry-After seconds: {RetryAfterSeconds}.")]
    private static partial void LogRateLimited(
        ILogger logger,
        double? retryAfterSeconds);
}
