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

internal sealed partial class IcijExtensionClient(
    IHttpClientFactory httpClientFactory,
    OffshoreLeaksHttpGate httpGate,
    IOptions<OffshoreLeaksAdapterOptions> optionsAccessor,
    TimeProvider timeProvider,
    ILogger<IcijExtensionClient> logger)
    : IIcijExtensionClient
{
    private const string EntitySchema =
        "https://offshoreleaks.icij.org/schema/oldb/entity";
    private static readonly string[] PropertyNames =
    [
        "jurisdiction",
        "jurisdiction_description",
        "country_codes",
        "countries",
        "sourceID",
        "name",
        "icij_id",
        "schema",
    ];
    private static readonly HashSet<string> PropertyAllowlist =
        new HashSet<string>(PropertyNames, StringComparer.Ordinal);
    private static readonly IReadOnlySet<HttpStatusCode> AcceptedStatuses =
        new HashSet<HttpStatusCode>
        {
            HttpStatusCode.OK,
        };
    private readonly OffshoreLeaksAdapterOptions _options = optionsAccessor.Value;

    public Task<IReadOnlyList<IcijEnrichedEntity>> EnrichAsync(
        IcijNamespaceDefinition @namespace,
        IReadOnlyList<IcijCandidate> candidates,
        OffshoreLeaksRequestBudget requestBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count is < 1
            || candidates.Count > _options.MaxExtensionIds)
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ extension request has an invalid number of identifiers.");
        }

        if (candidates.Select(candidate => candidate.NodeId).Distinct().Count()
            != candidates.Count)
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ extension request contains duplicate identifiers.");
        }

        return httpGate.ExecuteAsync(
            token => EnrichCoreAsync(
                @namespace,
                candidates,
                requestBudget,
                token),
            cancellationToken);
    }

    private async Task<IReadOnlyList<IcijEnrichedEntity>> EnrichCoreAsync(
        IcijNamespaceDefinition @namespace,
        IReadOnlyList<IcijCandidate> candidates,
        OffshoreLeaksRequestBudget requestBudget,
        CancellationToken cancellationToken)
    {
        requestBudget.Consume();
        var requestPath = CreateRequestPath(@namespace, candidates);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var client = httpClientFactory.CreateClient(
                IcijReconciliationClient.ClientName);
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
                    _options.MaxExtensionResponseBytes,
                    _options.MaxJsonDepth,
                    cancellationToken)
                .ConfigureAwait(false);
            return ParseEntities(
                @namespace,
                candidates,
                document.RootElement,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new ScreeningSourceUnavailableException(
                "The ICIJ extension service is unavailable.",
                exception);
        }
    }

    private static string CreateRequestPath(
        IcijNamespaceDefinition @namespace,
        IReadOnlyList<IcijCandidate> candidates)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("ids");
            writer.WriteStartArray();
            foreach (var candidate in candidates)
            {
                writer.WriteNumberValue(candidate.NodeId);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("properties");
            writer.WriteStartArray();
            foreach (var propertyName in PropertyNames)
            {
                writer.WriteStartObject();
                writer.WriteString("id", propertyName);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var extend = Uri.EscapeDataString(
            Encoding.UTF8.GetString(stream.ToArray()));
        return $"{@namespace.ReconcilePath}?extend={extend}";
    }

    private static IcijEnrichedEntity[] ParseEntities(
        IcijNamespaceDefinition @namespace,
        IReadOnlyList<IcijCandidate> candidates,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("meta", out var meta)
            || meta.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("rows", out var rows)
            || rows.ValueKind != JsonValueKind.Object)
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ extension response has an incompatible contract.");
        }

        ValidateMeta(meta);
        var candidatesById = candidates.ToDictionary(
            candidate => candidate.NodeId);
        var parsedRows = new Dictionary<long, IcijEnrichedEntity>();
        foreach (var row in rows.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!long.TryParse(
                    row.Name,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var nodeId)
                || !candidatesById.TryGetValue(nodeId, out var candidate))
            {
                throw new OffshoreLeaksAdapterException(
                    "The ICIJ extension response contains an unexpected identifier.");
            }

            if (!parsedRows.TryAdd(
                    nodeId,
                    ParseEntity(
                        @namespace,
                        candidate,
                        row.Value,
                        cancellationToken)))
            {
                throw new OffshoreLeaksAdapterException(
                    "The ICIJ extension response contains a duplicate row.");
            }
        }

        if (parsedRows.Count != candidates.Count)
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ extension response is missing a requested row.");
        }

        return candidates
            .Select(candidate => parsedRows[candidate.NodeId])
            .ToArray();
    }

    private static void ValidateMeta(JsonElement meta)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in meta.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String)
            {
                throw new OffshoreLeaksAdapterException(
                    "The ICIJ extension metadata is incompatible.");
            }

            var propertyName = id.GetString()!;
            if (!PropertyAllowlist.Contains(propertyName)
                || !seen.Add(propertyName))
            {
                throw new OffshoreLeaksAdapterException(
                    "The ICIJ extension metadata contains an unexpected property.");
            }
        }
    }

    private static IcijEnrichedEntity ParseEntity(
        IcijNamespaceDefinition @namespace,
        IcijCandidate candidate,
        JsonElement row,
        CancellationToken cancellationToken)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ extension row has an incompatible contract.");
        }

        var values = new Dictionary<string, ParsedProperty>(
            StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PropertyAllowlist.Contains(property.Name)
                || !values.TryAdd(
                    property.Name,
                    ParseProperty(
                        property.Name,
                        property.Value,
                        cancellationToken)))
            {
                throw new OffshoreLeaksAdapterException(
                    "An ICIJ extension row contains an unexpected property.");
            }
        }

        ValidateSchema(values);
        var extendedName = SingleValue(values, "name", required: false);
        var name = extendedName ?? candidate.Name;
        if (!NamesAreCompatible(candidate.Name, name))
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ extension row conflicts with its candidate name.");
        }

        var jurisdiction = SingleValue(
                values,
                "jurisdiction_description",
                required: false)
            ?? SingleValue(values, "jurisdiction", required: false);
        var countries = Values(values, "countries");
        var countryCodes = Values(values, "country_codes");
        var linkedTo = countries.Count > 0 ? countries : countryCodes;
        var omittedCount = Omitted(values, "countries");
        if (countries.Count == 0)
        {
            omittedCount += Omitted(values, "country_codes");
        }

        var icijId = SingleValue(values, "icij_id", required: false);
        return IcijEnrichedEntity.Create(
            @namespace.Value,
            candidate.NodeId,
            name,
            jurisdiction,
            linkedTo
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal),
            omittedCount,
            icijId);
    }

    private static ParsedProperty ParseProperty(
        string propertyName,
        JsonElement property,
        CancellationToken cancellationToken)
    {
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ extension property is not an array.");
        }

        var values = new List<string>();
        var omitted = 0;
        foreach (var item in property.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("str", out var valueElement)
                || valueElement.ValueKind != JsonValueKind.String)
            {
                throw new OffshoreLeaksAdapterException(
                    "An ICIJ extension value has an incompatible contract.");
            }

            var value = valueElement.GetString()!;
            if (!IsValidOptionalValue(value))
            {
                if (propertyName is "schema" or "name")
                {
                    throw new OffshoreLeaksAdapterException(
                        "An essential ICIJ extension value exceeds its limits.");
                }

                omitted++;
                continue;
            }

            values.Add(value.Normalize(NormalizationForm.FormC));
        }

        return new ParsedProperty(
            values
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            omitted);
    }

    private static bool IsValidOptionalValue(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.EnumerateRunes().Count()
            <= ScreeningHistoryLimits.FieldValueRunes
        && !value.EnumerateRunes().Any(rune =>
            Rune.GetUnicodeCategory(rune)
                == System.Globalization.UnicodeCategory.Control);

    private static void ValidateSchema(
        IReadOnlyDictionary<string, ParsedProperty> values)
    {
        var schema = Values(values, "schema");
        if (schema.Count != 1
            || !string.Equals(
                schema[0],
                EntitySchema,
                StringComparison.Ordinal))
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ extension row does not confirm the Entity schema.");
        }
    }

    private static string? SingleValue(
        IReadOnlyDictionary<string, ParsedProperty> properties,
        string propertyName,
        bool required)
    {
        var values = Values(properties, propertyName);
        if (values.Count == 0 && !required)
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new OffshoreLeaksAdapterException(
                "An ICIJ extension property has an incompatible cardinality.");
        }

        return values[0];
    }

    private static IReadOnlyList<string> Values(
        IReadOnlyDictionary<string, ParsedProperty> properties,
        string propertyName) =>
        properties.TryGetValue(propertyName, out var property)
            ? property.Values
            : [];

    private static int Omitted(
        IReadOnlyDictionary<string, ParsedProperty> properties,
        string propertyName) =>
        properties.TryGetValue(propertyName, out var property)
            ? property.OmittedCount
            : 0;

    private static bool NamesAreCompatible(string first, string second) =>
        string.Equals(
            EntityNameNormalizer.Normalize(first).Value,
            EntityNameNormalizer.Normalize(second).Value,
            StringComparison.Ordinal);

    [LoggerMessage(
        EventId = 4114,
        Level = LogLevel.Debug,
        Message = "ICIJ extension was rate limited; Retry-After seconds: {RetryAfterSeconds}.")]
    private static partial void LogRateLimited(
        ILogger logger,
        double? retryAfterSeconds);

    private sealed record ParsedProperty(
        IReadOnlyList<string> Values,
        int OmittedCount);
}
