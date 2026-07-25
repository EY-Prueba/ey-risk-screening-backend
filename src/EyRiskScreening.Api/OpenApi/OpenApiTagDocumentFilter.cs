using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EyRiskScreening.Api.OpenApi;

public sealed class OpenApiTagDocumentFilter : IDocumentFilter
{
    public void Apply(
        OpenApiDocument swaggerDoc,
        DocumentFilterContext context)
    {
        swaggerDoc.Tags = new HashSet<OpenApiTag>
        {
            new()
            {
                Name = "Authentication",
                Description = "Credential validation and JWT access-token issuance.",
            },
            new()
            {
                Name = "Screenings",
                Description =
                    "Execute screenings and retrieve immutable historical snapshots.",
            },
        };
    }
}
