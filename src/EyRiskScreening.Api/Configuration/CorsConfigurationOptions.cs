namespace EyRiskScreening.Api.Configuration;

public sealed class CorsConfigurationOptions
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}
