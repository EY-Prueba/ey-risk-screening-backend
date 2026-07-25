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
            new()
            {
                Name = "Suppliers",
                Description =
                    "Manage the shared supplier inventory used by client applications.",
            },
        };
    }
}
