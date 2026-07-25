using System.Text.Json;
using Microsoft.Playwright;

namespace EyRiskScreening.Api.Diagnostics;

internal static class PlaywrightSmoke
{
    public static async Task RunAsync()
    {
        using var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;

        try
        {
            browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args =
                    [
                        "--disable-background-networking",
                        "--disable-component-update",
                        "--disable-extensions",
                        "--disable-sync",
                    ],
                });
            context = await browser.NewContextAsync(
                new BrowserNewContextOptions
                {
                    Offline = true,
                    ServiceWorkers = ServiceWorkerPolicy.Block,
                });
            page = await context.NewPageAsync();
            await page.GotoAsync(
                "data:text/html,<title>EY Chromium Smoke</title>");
            var title = await page.TitleAsync();
            if (!string.Equals(
                    title,
                    "EY Chromium Smoke",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Chromium did not render the local smoke document.");
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "passed",
                browser = "chromium",
                browserVersion = browser.Version,
                executablePath = playwright.Chromium.ExecutablePath,
            }));
        }
        finally
        {
            if (page is not null)
            {
                await page.CloseAsync();
            }

            if (context is not null)
            {
                await context.CloseAsync();
            }

            if (browser is not null)
            {
                await browser.CloseAsync();
            }
        }
    }
}
