using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Infrastructure.Screening.WorldBank;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

internal static class WorldBankTestData
{
    public const string KendoHeaderTable =
        """
        <div class="k-grid-header"><table role="grid"><thead>
          <tr role="row">
            <th data-field="SUPP_NAME" rowspan="2">Firm Name</th>
            <th data-field="ADD_SUPP_INFO" rowspan="2"
                style="display:none">Additional Firm Info</th>
            <th data-field="SUPPLIER_ADDRESS" rowspan="2">Address</th>
            <th data-field="COUNTRY_NAME" rowspan="2">Country</th>
            <th colspan="2">Ineligibility Period</th>
            <th data-field="DEBAR_REASON" rowspan="2">Grounds</th>
          </tr>
          <tr role="row">
            <th data-field="DEBAR_FROM_DATE">From Date</th>
            <th data-field="DEBAR_TO_DATE">To Date</th>
          </tr>
        </thead></table></div>
        """;

    public static WorldBankHeaderCell[][] HeaderRows() =>
    [
        [
            Header(
                "SUPP_NAME",
                "Firm Name",
                row: 0,
                position: 0,
                rowSpan: 2),
            Header(
                "ADD_SUPP_INFO",
                "Additional Firm Info",
                row: 0,
                position: 1,
                rowSpan: 2,
                display: "none",
                hidden: true),
            Header(
                "SUPPLIER_ADDRESS",
                "Address",
                row: 0,
                position: 2,
                rowSpan: 2),
            Header(
                "COUNTRY_NAME",
                "Country",
                row: 0,
                position: 3,
                rowSpan: 2),
            Header(
                null,
                "Ineligibility Period",
                row: 0,
                position: 4,
                colSpan: 2),
            Header(
                "DEBAR_REASON",
                "Grounds",
                row: 0,
                position: 5,
                rowSpan: 2),
        ],
        [
            Header(
                "DEBAR_FROM_DATE",
                "From Date",
                row: 1,
                position: 0),
            Header(
                "DEBAR_TO_DATE",
                "To Date",
                row: 1,
                position: 1),
        ],
    ];

    public static string[] ValidRow(
        string firmName = "Acme Corporation",
        string additionalInfo = "Formerly Acme Trading",
        string address = "123 Example Avenue",
        string country = "Peru",
        string fromDate = "01-Jan-2024",
        string toDate = "Ongoing",
        string grounds = "Procurement violation") =>
    [
        firmName,
        additionalInfo,
        address,
        country,
        fromDate,
        toDate,
        grounds,
    ];

    public static WorldBankTableData Table(params string[][] rows) =>
        WorldBankTableData.Create(
            HeaderRows(),
            rows.Length == 0 ? [ValidRow()] : rows,
            4096);

    public static WorldBankHeaderCell Header(
        string? dataField,
        string text,
        int row,
        int position,
        int colSpan = 1,
        int rowSpan = 1,
        string display = "table-cell",
        bool hidden = false) =>
        new(
            dataField,
            text,
            row,
            position,
            colSpan,
            rowSpan,
            display,
            hidden);

    public static WorldBankAdapterOptions Options(
        string baseUrl = "http://127.0.0.1:1/") =>
        new()
        {
            BaseUrl = baseUrl,
            SnapshotTtlMinutes = 180,
            MaxRows = 10000,
            MaxRequestsPerRefresh = 64,
            MaxRenderedContentBytes = 8388608,
            BrowserHeadless = true,
            TableSelector = "#k-debarred-firms",
            RowSelector = "#k-debarred-firms .k-grid-content tbody tr",
            UserAgent = "EY-Risk-Screening/1.0",
        };

    public static WorldBankDomParser Parser(
        WorldBankAdapterOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()));

    public static ScreeningOptions ScreeningOptions(int timeoutSeconds = 30) =>
        new()
        {
            GlobalTimeoutSeconds = 40,
            Sources = Enum.GetValues<ScreeningSource>().ToDictionary(
                source => source,
                _ => new ScreeningSourceOptions
                {
                    MatchThreshold = 80,
                    TimeoutSeconds = timeoutSeconds,
                    ResultLimit = 100,
                }),
        };
}

internal sealed class FakeWorldBankBrowserClient(
    Func<CancellationToken, Task<WorldBankTableData>> load)
    : IWorldBankBrowserClient
{
    private int _loadCount;

    public int LoadCount => Volatile.Read(ref _loadCount);

    public Task<WorldBankTableData> LoadTableAsync(
        CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _loadCount);
        return load(cancellationToken);
    }
}

internal sealed class TestHostApplicationLifetime :
    IHostApplicationLifetime,
    IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => _stopping.Cancel();

    public void Dispose() => _stopping.Dispose();
}

internal sealed class WorldBankTestHostEnvironment(string environmentName)
    : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } =
        new NullFileProvider();
}
