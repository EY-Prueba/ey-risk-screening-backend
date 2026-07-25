namespace EyRiskScreening.IntegrationTests.Infrastructure;

internal static class OfacFixtureLoader
{
    public static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Ofac",
            fileName));

    public static MemoryStream Open(string fileName) =>
        new(System.Text.Encoding.UTF8.GetBytes(Read(fileName)));
}
