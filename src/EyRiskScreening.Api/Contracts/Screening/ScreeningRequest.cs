using System.ComponentModel.DataAnnotations;

namespace EyRiskScreening.Api.Contracts.Screening;

public sealed class ScreeningRequest
{
    [Required]
    public string? EntityName { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(3)]
    public IReadOnlyList<ScreeningSourceContract>? Sources { get; init; }
}
