namespace EyRiskScreening.Api.Diagnostics;

internal static class PlaywrightInstaller
{
    public static int Run(string[] arguments)
    {
        Environment.SetEnvironmentVariable(
            "PLAYWRIGHT_DRIVER_SEARCH_PATH",
            AppContext.BaseDirectory);
        return Microsoft.Playwright.Program.Main(arguments);
    }
}
