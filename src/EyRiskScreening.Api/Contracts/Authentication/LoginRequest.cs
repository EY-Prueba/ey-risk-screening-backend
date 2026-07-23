using System.ComponentModel.DataAnnotations;

namespace EyRiskScreening.Api.Contracts.Authentication;

public sealed class LoginRequest
{
    [Required]
    [StringLength(256)]
    public string UserName { get; init; } = string.Empty;

    [Required]
    [StringLength(256)]
    public string Password { get; init; } = string.Empty;
}
