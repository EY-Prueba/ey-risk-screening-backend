using System.Text;
using System.Text.Json;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;

namespace EyRiskScreening.Application.Screening;

public static class ScreeningSourceCandidatePersistenceGuard
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static void ValidateMatch(
        string referenceId,
        string name,
        IReadOnlyList<ScreeningSourceField> fields)
    {
        ScreeningHistoryGuard.RequiredText(
            referenceId,
            ScreeningHistoryLimits.ReferenceIdRunes,
            nameof(referenceId));
        ScreeningHistoryGuard.RequiredText(
            name,
            ScreeningHistoryLimits.MatchNameRunes,
            nameof(name));
        ScreeningHistoryGuard.RequiredText(
            EntityNameNormalizer.Normalize(name).Value,
            ScreeningHistoryLimits.MatchNameRunes,
            "normalizedName");

        if (fields is null)
        {
            throw new ScreeningHistoryValidationException(
                "Match fields must not be null.");
        }

        if (fields.Count > ScreeningHistoryLimits.MaximumFieldsPerMatch)
        {
            throw new ScreeningHistoryValidationException(
                "A match contains too many fields.");
        }

        var storedFields = new List<StoredField>(fields.Count);
        foreach (var field in fields)
        {
            if (field is null)
            {
                throw new ScreeningHistoryValidationException(
                    "Match fields must not contain null values.");
            }

            ScreeningHistoryGuard.RequiredText(
                field.Name,
                ScreeningHistoryLimits.FieldNameRunes,
                nameof(field.Name));
            ScreeningHistoryGuard.RequiredText(
                field.Value,
                ScreeningHistoryLimits.FieldValueRunes,
                nameof(field.Value));
            storedFields.Add(new StoredField(field.Name, field.Value));
        }

        var json = JsonSerializer.Serialize(storedFields, JsonOptions);
        if (Encoding.Unicode.GetByteCount(json)
            > ScreeningHistoryLimits.MaximumFieldsJsonBytes)
        {
            throw new ScreeningHistoryValidationException(
                "Serialized match fields exceed the persistence limit.");
        }
    }

    private sealed record StoredField(string Name, string Value);
}
