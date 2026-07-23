using System.Text.Json.Serialization;

namespace EyRiskScreening.Api.Contracts.Screening;

[JsonConverter(typeof(StrictJsonStringEnumConverter<ScreeningSourceContract>))]
public enum ScreeningSourceContract
{
    OffshoreLeaks = 0,
    WorldBank = 1,
    Ofac = 2,
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ScreeningSourceStatusContract>))]
public enum ScreeningSourceStatusContract
{
    Succeeded = 0,
    TimedOut = 1,
    Unavailable = 2,
    Failed = 3,
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ScreeningRunStatusContract>))]
public enum ScreeningRunStatusContract
{
    Completed = 0,
    PartiallyCompleted = 1,
    Failed = 2,
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ScreeningSourceErrorCodeContract>))]
public enum ScreeningSourceErrorCodeContract
{
    SourceTimedOut = 0,
    GlobalTimeout = 1,
    SourceUnavailable = 2,
    SourceFailed = 3,
}
