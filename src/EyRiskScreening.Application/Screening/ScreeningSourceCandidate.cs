namespace EyRiskScreening.Application.Screening;

public sealed record ScreeningSourceCandidate(
    string ReferenceId,
    string Name,
    IReadOnlyList<ScreeningSourceField> Fields,
    IReadOnlyList<ScreeningSourceAlternativeName> AlternativeNames)
{
    public ScreeningSourceCandidate(
        string referenceId,
        string name,
        IReadOnlyList<ScreeningSourceField> fields)
        : this(referenceId, name, fields, [])
    {
    }
}
