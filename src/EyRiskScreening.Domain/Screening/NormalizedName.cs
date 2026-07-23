namespace EyRiskScreening.Domain.Screening;

public readonly record struct NormalizedName(string Value)
{
    public override string ToString() => Value;
}
