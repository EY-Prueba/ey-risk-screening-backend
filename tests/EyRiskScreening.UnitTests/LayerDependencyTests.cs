using System.Xml.Linq;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class LayerDependencyTests
{
    private const string ProjectReferenceElement = "ProjectReference";

    public static TheoryData<string, string[]> ExpectedReferences => new()
    {
        {
            "src/EyRiskScreening.Domain/EyRiskScreening.Domain.csproj",
            []
        },
        {
            "src/EyRiskScreening.Application/EyRiskScreening.Application.csproj",
            ["src/EyRiskScreening.Domain/EyRiskScreening.Domain.csproj"]
        },
        {
            "src/EyRiskScreening.Infrastructure/EyRiskScreening.Infrastructure.csproj",
            [
                "src/EyRiskScreening.Application/EyRiskScreening.Application.csproj",
                "src/EyRiskScreening.Domain/EyRiskScreening.Domain.csproj",
            ]
        },
        {
            "src/EyRiskScreening.Api/EyRiskScreening.Api.csproj",
            [
                "src/EyRiskScreening.Application/EyRiskScreening.Application.csproj",
                "src/EyRiskScreening.Infrastructure/EyRiskScreening.Infrastructure.csproj",
            ]
        },
        {
            "tests/EyRiskScreening.UnitTests/EyRiskScreening.UnitTests.csproj",
            [
                "src/EyRiskScreening.Application/EyRiskScreening.Application.csproj",
                "src/EyRiskScreening.Domain/EyRiskScreening.Domain.csproj",
            ]
        },
        {
            "tests/EyRiskScreening.IntegrationTests/EyRiskScreening.IntegrationTests.csproj",
            ["src/EyRiskScreening.Api/EyRiskScreening.Api.csproj"]
        },
    };

    [Theory]
    [MemberData(nameof(ExpectedReferences))]
    public void ProjectHasOnlyAllowedProjectReferences(
        string projectPath,
        string[] expectedReferences)
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectFile = Path.Combine(repositoryRoot, NormalizePath(projectPath));
        var projectDirectory = Path.GetDirectoryName(projectFile)
            ?? throw new InvalidOperationException($"Project directory not found for '{projectFile}'.");

        var document = XDocument.Load(projectFile);
        var actualReferences = document
            .Descendants(ProjectReferenceElement)
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => NormalizeProjectReference(repositoryRoot, projectDirectory, reference!))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var normalizedExpectedReferences = expectedReferences
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(normalizedExpectedReferences, actualReferences);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root could not be located.");
    }

    private static string NormalizePath(string path) =>
        path
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

    private static string NormalizeProjectReference(
        string repositoryRoot,
        string projectDirectory,
        string projectReference)
    {
        var absolutePath = Path.GetFullPath(
            Path.Combine(projectDirectory, NormalizePath(projectReference)));
        var relativePath = Path.GetRelativePath(repositoryRoot, absolutePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        while (relativePath.StartsWith("./", StringComparison.Ordinal))
        {
            relativePath = relativePath[2..];
        }

        return relativePath;
    }
}
