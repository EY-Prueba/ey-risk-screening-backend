using System.Text.Json;
using System.Text.Json.Serialization;

namespace EyRiskScreening.Api.Contracts.Screening;

public sealed class StrictJsonStringEnumConverter<TEnum>
    : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public StrictJsonStringEnumConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}
