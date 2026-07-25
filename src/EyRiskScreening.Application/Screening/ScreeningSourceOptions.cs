namespace EyRiskScreening.Application.Screening;

public sealed class ScreeningSourceOptions
{
    public int MatchThreshold { get; init; }

    public int TimeoutSeconds { get; init; }

    public int ResultLimit { get; init; }
}
