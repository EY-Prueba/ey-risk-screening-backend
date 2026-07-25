using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal sealed class OfacDatasetProvider(
    IOfacClient client,
    IOptions<OfacAdapterOptions> optionsAccessor,
    ScreeningOptions screeningOptions,
    TimeProvider timeProvider,
    OfacBoundedFieldProjector fieldProjector,
    IHostApplicationLifetime applicationLifetime) : IDisposable
{
    private readonly object _refreshSync = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly OfacAdapterOptions _options = optionsAccessor.Value;
    private readonly TimeSpan _refreshTimeout = TimeSpan.FromSeconds(
        screeningOptions.Sources[ScreeningSource.Ofac].TimeoutSeconds);
    private OfacDatasetSnapshot? _snapshot;
    private Task<RefreshOutcome>? _refreshTask;

    internal Task RefreshCompletion
    {
        get
        {
            lock (_refreshSync)
            {
                return _refreshTask ?? Task.CompletedTask;
            }
        }
    }

    public async Task<OfacDatasetSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Volatile.Read(ref _snapshot);
        if (IsFresh(snapshot))
        {
            return snapshot!;
        }

        Task<RefreshOutcome> sharedRefreshTask;
        lock (_refreshSync)
        {
            snapshot = Volatile.Read(ref _snapshot);
            if (IsFresh(snapshot))
            {
                return snapshot!;
            }

            if (_refreshTask is null || _refreshTask.IsCompleted)
            {
                _refreshTask = RefreshSnapshotAsync();
            }

            sharedRefreshTask = _refreshTask;
        }

        var outcome = await sharedRefreshTask
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return outcome.GetSnapshotOrThrow();
    }

    private bool IsFresh(OfacDatasetSnapshot? snapshot) =>
        snapshot is not null && timeProvider.GetUtcNow() < snapshot.ExpiresAtUtc;

    private async Task<RefreshOutcome> RefreshSnapshotAsync()
    {
        using var timeoutCancellation = new CancellationTokenSource(
            _refreshTimeout,
            timeProvider);
        using var refreshCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellation.Token,
                applicationLifetime.ApplicationStopping,
                _disposeCancellation.Token);

        try
        {
            var snapshot = await LoadSnapshotAsync(
                    refreshCancellation.Token,
                    timeoutCancellation.Token,
                    applicationLifetime.ApplicationStopping,
                    _disposeCancellation.Token)
                .ConfigureAwait(false);
            Volatile.Write(ref _snapshot, snapshot);
            return RefreshOutcome.Success(snapshot);
        }
        catch (OperationCanceledException exception)
            when (applicationLifetime.ApplicationStopping.IsCancellationRequested
                  || _disposeCancellation.IsCancellationRequested)
        {
            return RefreshOutcome.Failure(exception);
        }
        catch (OperationCanceledException exception)
            when (timeoutCancellation.IsCancellationRequested)
        {
            return RefreshOutcome.Failure(new ScreeningSourceTimedOutException(
                "The shared OFAC dataset refresh reached its configured time limit.",
                exception));
        }
        catch (Exception exception)
        {
            return RefreshOutcome.Failure(exception);
        }
    }

    private async Task<OfacDatasetSnapshot> LoadSnapshotAsync(
        CancellationToken cancellationToken,
        CancellationToken timeoutCancellationToken,
        CancellationToken applicationStoppingToken,
        CancellationToken disposeCancellationToken)
    {
        using var siblingCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sdnTask = client.DownloadAsync(
            OfacDatasetKind.Sdn,
            siblingCancellation.Token);
        var consolidatedTask = client.DownloadAsync(
            OfacDatasetKind.Consolidated,
            siblingCancellation.Token);

        var firstCompleted = await Task
            .WhenAny(sdnTask, consolidatedTask)
            .ConfigureAwait(false);
        if (!firstCompleted.IsCompletedSuccessfully)
        {
            await siblingCancellation.CancelAsync().ConfigureAwait(false);
        }

        var sdnOutcome = await CaptureAsync(sdnTask).ConfigureAwait(false);
        if (!sdnOutcome.IsSuccess)
        {
            await siblingCancellation.CancelAsync().ConfigureAwait(false);
        }

        var consolidatedOutcome = await CaptureAsync(consolidatedTask)
            .ConfigureAwait(false);
        if (!consolidatedOutcome.IsSuccess)
        {
            await siblingCancellation.CancelAsync().ConfigureAwait(false);
        }

        ThrowForDownloadFailures(
            sdnOutcome.Exception,
            consolidatedOutcome.Exception,
            timeoutCancellationToken,
            applicationStoppingToken,
            disposeCancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var dataRetrievedAtUtc = timeProvider.GetUtcNow();
        var candidates = BuildCandidates(
            sdnOutcome.Records!,
            consolidatedOutcome.Records!,
            dataRetrievedAtUtc,
            cancellationToken);
        var loadedAtUtc = timeProvider.GetUtcNow();
        return new OfacDatasetSnapshot(
            loadedAtUtc,
            loadedAtUtc.AddMinutes(_options.SnapshotTtlMinutes),
            candidates);
    }

    private static async Task<DownloadOutcome> CaptureAsync(
        Task<IReadOnlyList<OfacRecord>> task)
    {
        try
        {
            return DownloadOutcome.Success(
                await task.ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return DownloadOutcome.Failure(exception);
        }
    }

    private static void ThrowForDownloadFailures(
        Exception? sdnException,
        Exception? consolidatedException,
        CancellationToken timeoutCancellationToken,
        CancellationToken applicationStoppingToken,
        CancellationToken disposeCancellationToken)
    {
        if (sdnException is null && consolidatedException is null)
        {
            return;
        }

        applicationStoppingToken.ThrowIfCancellationRequested();
        disposeCancellationToken.ThrowIfCancellationRequested();
        var exceptions = new[] { sdnException, consolidatedException }
            .Where(exception => exception is not null)
            .Cast<Exception>()
            .ToArray();

        var failure = exceptions.FirstOrDefault(
                exception => exception is OfacAdapterException)
            ?? exceptions.FirstOrDefault(
                exception => exception is not OperationCanceledException
                    and not ScreeningSourceTimedOutException
                    and not ScreeningSourceUnavailableException);
        if (failure is not null)
        {
            if (failure is not OfacAdapterException)
            {
                failure = new OfacAdapterException(
                    "The OFAC dataset refresh failed.",
                    failure);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        failure = exceptions.FirstOrDefault(
                exception => exception is ScreeningSourceTimedOutException)
            ?? (timeoutCancellationToken.IsCancellationRequested
                ? new ScreeningSourceTimedOutException(
                    "The shared OFAC dataset refresh reached its configured time limit.")
                : null)
            ?? exceptions.FirstOrDefault(
                exception => exception is ScreeningSourceUnavailableException)
            ?? new OfacAdapterException(
                "The OFAC dataset refresh was cancelled internally.");

        ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private ReadOnlyCollection<ScreeningSourceCandidate> BuildCandidates(
        IReadOnlyList<OfacRecord> sdnRecords,
        IReadOnlyList<OfacRecord> consolidatedRecords,
        DateTimeOffset dataRetrievedAtUtc,
        CancellationToken cancellationToken)
    {
        var recordsByUid = new Dictionary<string, List<OfacRecord>>(
            StringComparer.Ordinal);
        AddRecords(recordsByUid, sdnRecords, cancellationToken);
        AddRecords(recordsByUid, consolidatedRecords, cancellationToken);

        var candidates = new List<ScreeningSourceCandidate>(recordsByUid.Count);
        foreach (var pair in recordsByUid.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(BuildCandidate(
                pair.Value,
                dataRetrievedAtUtc,
                cancellationToken));
        }

        return new ReadOnlyCollection<ScreeningSourceCandidate>(candidates);
    }

    private void AddRecords(
        Dictionary<string, List<OfacRecord>> recordsByUid,
        IReadOnlyList<OfacRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!recordsByUid.TryGetValue(record.Uid, out var groupedRecords))
            {
                groupedRecords = [];
                recordsByUid.Add(record.Uid, groupedRecords);
                if (recordsByUid.Count > _options.MaxCandidates)
                {
                    throw new OfacAdapterException(
                        "The combined OFAC dataset exceeds the configured candidate limit.");
                }
            }
            else if (groupedRecords.Any(existing =>
                         !OfacRecord.HasCompatibleIdentity(existing, record)))
            {
                throw new OfacAdapterException(
                    "The OFAC datasets contain conflicting records for one UID.");
            }

            if (!groupedRecords.Any(existing =>
                    OfacRecord.AreEquivalent(existing, record)))
            {
                groupedRecords.Add(record);
            }
        }
    }

    private ScreeningSourceCandidate BuildCandidate(
        List<OfacRecord> records,
        DateTimeOffset dataRetrievedAtUtc,
        CancellationToken cancellationToken)
    {
        var primary = records[0];
        ValidatePersistableText(
            primary.Uid,
            ScreeningHistoryLimits.ReferenceIdRunes,
            nameof(primary.Uid));
        ValidatePersistableText(
            primary.PrimaryName,
            ScreeningHistoryLimits.MatchNameRunes,
            nameof(primary.PrimaryName));

        var aliases = new List<ScreeningSourceAlternativeName>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal)
        {
            EntityNameNormalizer.Normalize(primary.PrimaryName).Value,
        };
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddAliasIfDistinct(
                record.PrimaryName,
                [],
                seenNames,
                aliases,
                cancellationToken);

            foreach (var alias in record.Aliases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddAliasIfDistinct(
                    alias.Name,
                    CreateAliasFields(alias),
                    seenNames,
                    aliases,
                    cancellationToken);
            }
        }

        var fields = CreateCandidateFields(
            records,
            primary.PrimaryName,
            dataRetrievedAtUtc,
            cancellationToken);
        ValidatePersistableMatch(primary.Uid, primary.PrimaryName, fields);
        foreach (var alias in aliases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePersistableMatch(
                primary.Uid,
                alias.Name,
                CombineFields(fields, alias.Fields, cancellationToken));
        }

        return new ScreeningSourceCandidate(
            primary.Uid,
            primary.PrimaryName,
            fields,
            new ReadOnlyCollection<ScreeningSourceAlternativeName>(aliases));
    }

    private void AddAliasIfDistinct(
        string name,
        IReadOnlyList<ScreeningSourceField> fields,
        HashSet<string> seenNames,
        List<ScreeningSourceAlternativeName> aliases,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePersistableText(
            name,
            ScreeningHistoryLimits.MatchNameRunes,
            nameof(name));
        var normalizedName = EntityNameNormalizer.Normalize(name).Value;
        if (normalizedName.Length == 0 || !seenNames.Add(normalizedName))
        {
            return;
        }

        aliases.Add(new ScreeningSourceAlternativeName(name, fields));
        if (aliases.Count + 1 > _options.MaxNamesPerCandidate)
        {
            throw new OfacAdapterException(
                "A combined OFAC record exceeds the configured name limit.");
        }
    }

    private ReadOnlyCollection<ScreeningSourceField> CreateCandidateFields(
        IReadOnlyList<OfacRecord> records,
        string primaryName,
        DateTimeOffset dataRetrievedAtUtc,
        CancellationToken cancellationToken)
    {
        var addresses = new List<OfacAddress>();
        var seenAddresses = new HashSet<OfacAddress>();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var address in record.Addresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seenAddresses.Add(address))
                {
                    addresses.Add(address);
                }
            }
        }

        var list = fieldProjector.ProjectSorted(
            "List",
            records.Select(record => record.ListName),
            cancellationToken);
        var type = fieldProjector.ProjectSorted(
            "Type",
            records.Select(record => record.Type),
            cancellationToken);
        if (type.OmittedCount > 0)
        {
            throw new OfacAdapterException(
                "An essential OFAC field exceeds the persistence limit.");
        }

        var programs = fieldProjector.ProjectSorted(
            "Programs",
            EnumeratePrograms(records, cancellationToken),
            cancellationToken);
        var addressProjection = fieldProjector.ProjectRepresentative(
            "Address",
            addresses.Select(value => value.FormattedAddress),
            cancellationToken);
        var countries = fieldProjector.ProjectSorted(
            "Country",
            addresses.Select(value => value.Country),
            cancellationToken);
        var nationalities = fieldProjector.ProjectSorted(
            "Nationality",
            EnumerateNationalities(records, cancellationToken),
            cancellationToken);

        var fields = new List<ScreeningSourceField>
        {
            new("PrimaryName", primaryName),
            new(
                "AddressCount",
                addresses.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
            new(
                "DataRetrievedAtUtc",
                dataRetrievedAtUtc.ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture)),
        };
        AddProjection(
            fields,
            "Type",
            countName: null,
            omittedCountName: null,
            type);
        AddProjection(
            fields,
            "List",
            "ListCount",
            "ListsOmittedCount",
            list);
        AddProjection(
            fields,
            "Programs",
            "ProgramCount",
            "ProgramsOmittedCount",
            programs);
        AddProjection(
            fields,
            "Address",
            countName: null,
            "AddressesOmittedCount",
            addressProjection);
        AddProjection(
            fields,
            "Country",
            "CountryCount",
            "CountriesOmittedCount",
            countries);
        AddProjection(
            fields,
            "Nationality",
            "NationalityCount",
            "NationalitiesOmittedCount",
            nationalities);
        return new ReadOnlyCollection<ScreeningSourceField>(fields);
    }

    private static IEnumerable<string> EnumeratePrograms(
        IReadOnlyList<OfacRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var program in record.Programs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return program;
            }
        }
    }

    private static IEnumerable<string> EnumerateNationalities(
        IReadOnlyList<OfacRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var nationality in record.Nationalities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return nationality;
            }
        }
    }

    private static ReadOnlyCollection<ScreeningSourceField> CreateAliasFields(
        OfacAlias alias)
    {
        var fields = new List<ScreeningSourceField>();
        AddIfPresent(fields, "AliasType", alias.Type);
        AddIfPresent(fields, "AliasQuality", alias.Quality);
        return new ReadOnlyCollection<ScreeningSourceField>(fields);
    }

    private static void AddIfPresent(
        List<ScreeningSourceField> fields,
        string name,
        string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields.Add(new ScreeningSourceField(name, value));
        }
    }

    private static void AddProjection(
        List<ScreeningSourceField> fields,
        string valueName,
        string? countName,
        string? omittedCountName,
        OfacBoundedFieldProjection projection)
    {
        AddIfPresent(fields, valueName, projection.Value);
        if (countName is not null
            && (projection.TotalCount > 1 || projection.OmittedCount > 0))
        {
            fields.Add(new ScreeningSourceField(
                countName,
                projection.TotalCountText));
        }

        if (omittedCountName is not null && projection.OmittedCount > 0)
        {
            fields.Add(new ScreeningSourceField(
                omittedCountName,
                projection.OmittedCountText));
        }
    }

    private static List<ScreeningSourceField> CombineFields(
        ReadOnlyCollection<ScreeningSourceField> candidateFields,
        IReadOnlyList<ScreeningSourceField> aliasFields,
        CancellationToken cancellationToken)
    {
        var fields = new List<ScreeningSourceField>(
            candidateFields.Count + aliasFields.Count);
        foreach (var field in candidateFields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fields.Add(field);
        }

        foreach (var field in aliasFields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fields.Add(field);
        }

        return fields;
    }

    private static void ValidatePersistableMatch(
        string referenceId,
        string name,
        IReadOnlyList<ScreeningSourceField> fields)
    {
        try
        {
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                referenceId,
                name,
                fields);
        }
        catch (ScreeningHistoryValidationException exception)
        {
            throw new OfacAdapterException(
                "An OFAC record exceeds the screening history limits.",
                exception);
        }
    }

    private static void ValidatePersistableText(
        string value,
        int maximumRunes,
        string parameterName)
    {
        try
        {
            ScreeningHistoryGuard.RequiredText(
                value,
                maximumRunes,
                parameterName);
        }
        catch (ScreeningHistoryValidationException exception)
        {
            throw new OfacAdapterException(
                "An OFAC record exceeds the screening history limits.",
                exception);
        }
    }

    private sealed record DownloadOutcome(
        IReadOnlyList<OfacRecord>? Records,
        Exception? Exception)
    {
        public bool IsSuccess => Exception is null;

        public static DownloadOutcome Success(IReadOnlyList<OfacRecord> records) =>
            new(records, null);

        public static DownloadOutcome Failure(Exception exception) =>
            new(null, exception);
    }

    private sealed record RefreshOutcome(
        OfacDatasetSnapshot? Snapshot,
        Exception? Exception)
    {
        public static RefreshOutcome Success(OfacDatasetSnapshot snapshot) =>
            new(snapshot, null);

        public static RefreshOutcome Failure(Exception exception) =>
            new(null, exception);

        public OfacDatasetSnapshot GetSnapshotOrThrow()
        {
            if (Exception is not null)
            {
                ExceptionDispatchInfo.Capture(Exception).Throw();
            }

            return Snapshot!;
        }
    }

    public void Dispose()
    {
        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
    }
}

internal sealed class OfacDatasetSnapshot
{
    public OfacDatasetSnapshot(
        DateTimeOffset loadedAtUtc,
        DateTimeOffset expiresAtUtc,
        IReadOnlyList<ScreeningSourceCandidate> candidates)
    {
        LoadedAtUtc = loadedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        Candidates = candidates;
    }

    public DateTimeOffset LoadedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public IReadOnlyList<ScreeningSourceCandidate> Candidates { get; }
}
