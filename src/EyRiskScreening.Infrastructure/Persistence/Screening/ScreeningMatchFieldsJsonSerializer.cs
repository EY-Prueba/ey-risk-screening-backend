using System.Text;
using System.Text.Json;
using EyRiskScreening.Domain.Screening.History;

namespace EyRiskScreening.Infrastructure.Persistence.Screening;

internal static class ScreeningMatchFieldsJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static string Serialize(
        IReadOnlyList<ScreeningMatchFieldSnapshot> fields,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storedFields = new List<StoredField>(fields.Count);
        foreach (var field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            storedFields.Add(new StoredField(field.Name, field.Value));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(storedFields, SerializerOptions);
        if (Encoding.Unicode.GetByteCount(json)
            > ScreeningHistoryLimits.MaximumFieldsJsonBytes)
        {
            throw new ScreeningHistoryValidationException(
                "Serialized match fields exceed the supported JSON size.");
        }

        return json;
    }

    public static IReadOnlyList<ScreeningMatchFieldSnapshot> Deserialize(
        string json,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding.Unicode.GetByteCount(json)
            > ScreeningHistoryLimits.MaximumFieldsJsonBytes)
        {
            throw new ScreeningHistoryValidationException(
                "Stored match fields exceed the supported JSON size.");
        }

        var storedFields = JsonSerializer.Deserialize<List<StoredField>>(
            json,
            SerializerOptions)
            ?? throw new ScreeningHistoryValidationException(
                "Stored match fields are not a JSON array.");
        var fields = new List<ScreeningMatchFieldSnapshot>(storedFields.Count);
        foreach (var field in storedFields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fields.Add(new ScreeningMatchFieldSnapshot(field.Name, field.Value));
        }

        return fields;
    }

    private sealed record StoredField(string Name, string Value);
}
