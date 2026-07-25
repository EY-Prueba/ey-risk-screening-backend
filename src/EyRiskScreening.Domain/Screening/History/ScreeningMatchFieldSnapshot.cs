namespace EyRiskScreening.Domain.Screening.History;

public sealed class ScreeningMatchFieldSnapshot
{
    public ScreeningMatchFieldSnapshot(string name, string value)
    {
        ScreeningHistoryGuard.RequiredText(
            name,
            ScreeningHistoryLimits.FieldNameRunes,
            nameof(name));
        ScreeningHistoryGuard.RequiredText(
            value,
            ScreeningHistoryLimits.FieldValueRunes,
            nameof(value));

        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }
}
