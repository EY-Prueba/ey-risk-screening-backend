using System.Collections.Concurrent;
using System.Text.Json;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal sealed partial class WorldBankBrowserClient(
    IOptions<WorldBankAdapterOptions> optionsAccessor,
    ScreeningOptions screeningOptions,
    TimeProvider timeProvider,
    IHostEnvironment environment,
    ILogger<WorldBankBrowserClient> logger) : IWorldBankBrowserClient
{
    private static readonly HashSet<string> AllowedResourceTypes =
        new(StringComparer.Ordinal)
        {
            "document",
            "script",
            "xhr",
            "fetch",
            "stylesheet",
        };
    private static readonly HashSet<string> ProductionHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "projects.worldbank.org",
            "www.worldbank.org",
            "apigwext.worldbank.org",
        };
    private readonly WorldBankAdapterOptions _options = optionsAccessor.Value;
    private readonly float _timeoutMilliseconds =
        screeningOptions.Sources[ScreeningSource.WorldBank].TimeoutSeconds * 1000F;
    private readonly WorldBankCleanupCoordinator _cleanupCoordinator = new(
        timeProvider,
        TimeSpan.FromSeconds(optionsAccessor.Value.CleanupTimeoutSeconds),
        logger);

    public async Task<WorldBankTableData> LoadTableAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;
        var blockedByType = 0;
        var blockedByHost = 0;
        var requestCount = 0;
        var violations = new ConcurrentQueue<Exception>();
        WorldBankTableData? result = null;
        var cleanupSucceeded = true;

        try
        {
            playwright = await AwaitResourceOperationAsync(
                    Microsoft.Playwright.Playwright.CreateAsync(),
                    value => Task.Run(value.Dispose, CancellationToken.None),
                    cancellationToken)
                .ConfigureAwait(false);
            browser = await AwaitResourceOperationAsync(
                    playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions
                    {
                        Headless = _options.BrowserHeadless,
                        Args =
                        [
                            "--disable-background-networking",
                            "--disable-component-update",
                            "--disable-extensions",
                            "--disable-sync",
                        ],
                    }),
                    value => value.CloseAsync(),
                    cancellationToken)
                .ConfigureAwait(false);
            context = await AwaitResourceOperationAsync(
                    browser.NewContextAsync(
                    new BrowserNewContextOptions
                    {
                        AcceptDownloads = false,
                        IgnoreHTTPSErrors = false,
                        JavaScriptEnabled = true,
                        ServiceWorkers = ServiceWorkerPolicy.Block,
                        UserAgent = _options.UserAgent,
                        Permissions = [],
                    }),
                    value => value.CloseAsync(),
                    cancellationToken)
                .ConfigureAwait(false);
            page = await AwaitResourceOperationAsync(
                    context.NewPageAsync(),
                    value => value.CloseAsync(),
                    cancellationToken)
                .ConfigureAwait(false);
            page.SetDefaultTimeout(_timeoutMilliseconds);
            page.SetDefaultNavigationTimeout(_timeoutMilliseconds);
            context.Page += (_, createdPage) =>
            {
                if (!ReferenceEquals(createdPage, page))
                {
                    violations.Enqueue(new WorldBankAdapterException(
                        "The World Bank page opened an unexpected browser page."));
                }
            };
            await AwaitPageOperationAsync(
                    context.AddInitScriptAsync(
                    """
                    (() => {
                      const blockedConstructor = class {
                        constructor() {
                          throw new DOMException(
                            'Blocked by browser policy.',
                            'SecurityError');
                        }
                      };
                      for (const name of [
                        'WebSocket',
                        'EventSource',
                        'Worker',
                        'SharedWorker'
                      ]) {
                        Object.defineProperty(globalThis, name, {
                          configurable: false,
                          writable: false,
                          value: blockedConstructor
                        });
                      }
                      Object.defineProperty(globalThis, 'open', {
                        configurable: false,
                        writable: false,
                        value: () => null
                      });
                      Object.defineProperty(navigator, 'sendBeacon', {
                        configurable: false,
                        writable: false,
                        value: () => false
                      });
                    })();
                    """),
                    cancellationToken)
                .ConfigureAwait(false);

            await AwaitPageOperationAsync(
                    context.RouteAsync(
                "**/*",
                async route =>
                {
                    var currentCount = Interlocked.Increment(ref requestCount);
                    if (currentCount > _options.MaxRequestsPerRefresh)
                    {
                        violations.Enqueue(new WorldBankAdapterException(
                            "The World Bank refresh exceeded its request limit."));
                        await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
                        return;
                    }

                    if (!AllowedResourceTypes.Contains(route.Request.ResourceType))
                    {
                        _ = Interlocked.Increment(ref blockedByType);
                        await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
                        return;
                    }

                    if (!IsAllowedRequestUri(route.Request.Url))
                    {
                        _ = Interlocked.Increment(ref blockedByHost);
                        await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
                        return;
                    }

                    await route.ContinueAsync().ConfigureAwait(false);
                }),
                    cancellationToken)
                .ConfigureAwait(false);

            var navigationTask = page.GotoAsync(
                _options.BaseUrl,
                new PageGotoOptions
                {
                    Timeout = _timeoutMilliseconds,
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                });
            var response = await AwaitPageOperationAsync(
                    navigationTask,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowViolation(violations);
            ValidateNavigationResponse(response);
            ValidateActivePage(page);

            var tableCount = await AwaitPageOperationAsync(
                    page.Locator(_options.TableSelector).CountAsync(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (tableCount != 1)
            {
                throw new WorldBankAdapterException(
                    "The expected World Bank table is absent or ambiguous.");
            }

            await AwaitPageOperationAsync(
                    page.WaitForFunctionAsync(
                        """
                        selectors => {
                          const rows = document.querySelectorAll(selectors.rows);
                          const grid = document.querySelector(selectors.table);
                          const loading = grid &&
                            /\bLoading\b/i.test(grid.textContent || '');
                          if (loading || rows.length === 0) {
                            window.__eyWorldBankRowCount = -1;
                            window.__eyWorldBankStableFrames = 0;
                            return false;
                          }
                          if (window.__eyWorldBankRowCount === rows.length) {
                            window.__eyWorldBankStableFrames =
                              (window.__eyWorldBankStableFrames || 0) + 1;
                          } else {
                            window.__eyWorldBankRowCount = rows.length;
                            window.__eyWorldBankStableFrames = 0;
                          }
                          return window.__eyWorldBankStableFrames >= 2;
                        }
                        """,
                        new
                        {
                            rows = _options.RowSelector,
                            table = _options.TableSelector,
                        },
                        new PageWaitForFunctionOptions
                        {
                            Timeout = _timeoutMilliseconds,
                        }),
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowViolation(violations);
            ValidateActivePage(page);

            var rowLocator = page.Locator(_options.RowSelector);
            var rowCount = await AwaitPageOperationAsync(
                    rowLocator.CountAsync(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (rowCount > _options.MaxRows)
            {
                throw new WorldBankAdapterException(
                    "The rendered World Bank table exceeds the configured row limit.");
            }

            var renderedBytes = await AwaitPageOperationAsync(
                    page.Locator(_options.TableSelector).EvaluateAsync<long>(
                        "element => new TextEncoder().encode(element.innerText || '').length"),
                    cancellationToken)
                .ConfigureAwait(false);
            if (renderedBytes > _options.MaxRenderedContentBytes)
            {
                throw new WorldBankAdapterException(
                    "The rendered World Bank table exceeds the configured content limit.");
            }

            var headerRows = await AwaitPageOperationAsync(
                    ExtractHeaderRowsAsync(page),
                    cancellationToken)
                .ConfigureAwait(false);
            var rows = await AwaitPageOperationAsync(
                    rowLocator.EvaluateAllAsync<string[][]>(
                        """
                        elements => elements.map(row =>
                          Array.from(row.children, cell => cell.textContent || ''))
                        """),
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowViolation(violations);
            ValidateActivePage(page);
            LogBlockedResources(logger, blockedByType, blockedByHost);
            result = WorldBankTableData.Create(
                headerRows,
                rows,
                renderedBytes);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            throw new ScreeningSourceTimedOutException(
                "The rendered World Bank table did not become available in time.",
                exception);
        }
        catch (WorldBankAdapterException)
        {
            throw;
        }
        catch (PlaywrightException exception)
        {
            throw new ScreeningSourceUnavailableException(
                "The World Bank browser operation is unavailable.",
                exception);
        }
        finally
        {
            cleanupSucceeded = await CleanupAsync(
                    page,
                    context,
                    browser,
                    playwright)
                .ConfigureAwait(false);
        }

        if (!cleanupSucceeded)
        {
            throw new ScreeningSourceUnavailableException(
                "The World Bank browser resources could not be finalized.");
        }

        ThrowViolation(violations);
        return result
            ?? throw new InvalidOperationException(
                "A completed World Bank browser load must contain table data.");
    }

    private bool IsAllowedRequestUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0
            || uri.Fragment.Length > 0)
        {
            return false;
        }

        if (environment.IsEnvironment("Testing"))
        {
            var configured = new Uri(_options.BaseUrl, UriKind.Absolute);
            return uri.IsLoopback
                && string.Equals(
                    uri.Scheme,
                    configured.Scheme,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    uri.Host,
                    configured.Host,
                    StringComparison.OrdinalIgnoreCase)
                && uri.Port == configured.Port;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && ProductionHosts.Contains(uri.Host);
    }

    private void ValidateActivePage(IPage page)
    {
        if (!IsAllowedRequestUri(page.Url))
        {
            throw new WorldBankAdapterException(
                "The World Bank page navigated outside the approved origin.");
        }
    }

    private static void ValidateNavigationResponse(IResponse? response)
    {
        if (response is null)
        {
            throw new WorldBankAdapterException(
                "The World Bank navigation returned no response.");
        }

        if (response.Status == StatusCodes.Status408RequestTimeout)
        {
            throw new ScreeningSourceTimedOutException(
                "The World Bank navigation timed out.");
        }

        if (response.Status == StatusCodes.Status429TooManyRequests
            || response.Status >= StatusCodes.Status500InternalServerError)
        {
            throw new ScreeningSourceUnavailableException(
                "The World Bank page is temporarily unavailable.");
        }

        if (response.Status is < 200 or >= 400)
        {
            throw new WorldBankAdapterException(
                "The World Bank navigation was rejected.");
        }
    }

    private static void ThrowViolation(ConcurrentQueue<Exception> violations)
    {
        if (violations.TryDequeue(out var exception))
        {
            throw exception;
        }
    }

    private Task<IReadOnlyList<IReadOnlyList<WorldBankHeaderCell>>>
        ExtractHeaderRowsAsync(IPage page) =>
        ExtractHeaderRowsCoreAsync(
            page.Locator(
                $"{_options.TableSelector} .k-grid-header thead > tr"));

    private static async Task<
        IReadOnlyList<IReadOnlyList<WorldBankHeaderCell>>>
        ExtractHeaderRowsCoreAsync(ILocator rowLocator)
    {
        var json = await rowLocator.EvaluateAllAsync<string>(
                """
                rows => JSON.stringify(rows.map((row, rowIndex) => ({
                  rowIndex,
                  headers: Array.from(
                    row.children,
                    element => element.tagName === 'TH' ? element : null)
                    .filter(element => element !== null)
                    .map((header, position) => {
                      const style = getComputedStyle(header);
                      return {
                        dataField: header.getAttribute('data-field'),
                        textContent: header.textContent || '',
                        rowIndex,
                        position,
                        colSpan: header.colSpan,
                        rowSpan: header.rowSpan,
                        display: style.display,
                        hidden: header.hidden
                          || style.display === 'none'
                          || style.visibility === 'hidden'
                      };
                    })
                })))
                """)
            .ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw InvalidHeaderMetadata();
            }

            var rows = new List<IReadOnlyList<WorldBankHeaderCell>>();
            foreach (var rowElement in document.RootElement.EnumerateArray())
            {
                var rowIndex = RequiredInt32(rowElement, "rowIndex");
                var headersElement = RequiredArray(rowElement, "headers");
                var headers = new List<WorldBankHeaderCell>();
                foreach (var headerElement in headersElement.EnumerateArray())
                {
                    var dataField = OptionalString(headerElement, "dataField");
                    var textContent = RequiredString(
                        headerElement,
                        "textContent");
                    headers.Add(new WorldBankHeaderCell(
                        dataField,
                        WorldBankTextNormalizer.Normalize(textContent),
                        RequiredInt32(headerElement, "rowIndex"),
                        RequiredInt32(headerElement, "position"),
                        RequiredInt32(headerElement, "colSpan"),
                        RequiredInt32(headerElement, "rowSpan"),
                        RequiredString(headerElement, "display"),
                        RequiredBoolean(headerElement, "hidden")));
                }

                if (rowIndex != rows.Count)
                {
                    throw InvalidHeaderMetadata();
                }

                rows.Add(headers);
            }

            return rows;
        }
        catch (JsonException exception)
        {
            throw new WorldBankAdapterException(
                "The rendered World Bank header metadata is invalid.",
                exception);
        }
    }

    private static JsonElement RequiredArray(
        JsonElement parent,
        string propertyName)
    {
        var value = RequiredProperty(parent, propertyName);
        return value.ValueKind == JsonValueKind.Array
            ? value
            : throw InvalidHeaderMetadata();
    }

    private static string RequiredString(
        JsonElement parent,
        string propertyName)
    {
        var value = RequiredProperty(parent, propertyName);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw InvalidHeaderMetadata();
    }

    private static string? OptionalString(
        JsonElement parent,
        string propertyName)
    {
        var value = RequiredProperty(parent, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw InvalidHeaderMetadata(),
        };
    }

    private static int RequiredInt32(
        JsonElement parent,
        string propertyName)
    {
        var value = RequiredProperty(parent, propertyName);
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
                ? result
                : throw InvalidHeaderMetadata();
    }

    private static bool RequiredBoolean(
        JsonElement parent,
        string propertyName)
    {
        var value = RequiredProperty(parent, propertyName);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw InvalidHeaderMetadata();
    }

    private static JsonElement RequiredProperty(
        JsonElement parent,
        string propertyName) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(propertyName, out var value)
            ? value
            : throw InvalidHeaderMetadata();

    private static WorldBankAdapterException InvalidHeaderMetadata() =>
        new("The rendered World Bank header metadata is invalid.");

    private async Task<T> AwaitPageOperationAsync<T>(
        Task<T> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            _cleanupCoordinator.Track(operation, "browser-operation");
            throw;
        }
    }

    private async Task<T> AwaitResourceOperationAsync<T>(
        Task<T> operation,
        Func<T, Task> lateResultCleanup,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            _cleanupCoordinator.Track(
                operation,
                "browser-resource-creation",
                lateResultCleanup);
            throw;
        }
    }

    private async Task AwaitPageOperationAsync(
        Task operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            _cleanupCoordinator.Track(operation, "browser-operation");
            throw;
        }
    }

    private async Task<bool> CleanupAsync(
        IPage? page,
        IBrowserContext? context,
        IBrowser? browser,
        IPlaywright? playwright)
    {
        var budget = _cleanupCoordinator.CreateBudget();
        var succeeded = true;
        if (page is not null)
        {
            succeeded &= await _cleanupCoordinator.RunAsync(
                    () => page.CloseAsync(),
                    "page-close",
                    budget)
                .ConfigureAwait(false);
        }

        if (context is not null)
        {
            succeeded &= await _cleanupCoordinator.RunAsync(
                    () => context.ClearCookiesAsync(),
                    "context-clear-cookies",
                    budget)
                .ConfigureAwait(false);
            succeeded &= await _cleanupCoordinator.RunAsync(
                    () => context.CloseAsync(),
                    "context-close",
                    budget)
                .ConfigureAwait(false);
        }

        if (browser is not null)
        {
            succeeded &= await _cleanupCoordinator.RunAsync(
                    () => browser.CloseAsync(),
                    "browser-close",
                    budget)
                .ConfigureAwait(false);
        }

        if (playwright is not null)
        {
            succeeded &= await _cleanupCoordinator.RunSynchronousAsync(
                    playwright.Dispose,
                    "playwright-dispose",
                    budget)
                .ConfigureAwait(false);
        }

        return succeeded;
    }

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Debug,
        Message = "World Bank refresh blocked {BlockedByType} resources by type and {BlockedByHost} resources by host.")]
    private static partial void LogBlockedResources(
        ILogger logger,
        int blockedByType,
        int blockedByHost);
}
