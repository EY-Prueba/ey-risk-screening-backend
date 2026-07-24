using System.Text;
using EyRiskScreening.Domain.Screening.History;
using EyRiskScreening.Infrastructure.Screening.Ofac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EyRiskScreening.IntegrationTests;

public sealed class OfacBoundedFieldProjectorTests
{
    [Fact]
    public void ValuesThatFitAreDeduplicatedAndSortedDeterministically()
    {
        var projector = CreateProjector();

        var projection = projector.ProjectSorted(
            "Programs",
            ["Zulu", "Alpha", "Zulu", "  "],
            TestContext.Current.CancellationToken);

        Assert.Equal("Alpha; Zulu", projection.Value);
        Assert.Equal(2, projection.TotalCount);
        Assert.Equal(0, projection.OmittedCount);
    }

    [Fact]
    public void AccumulationPastRuneBudgetOmitsOnlyCompleteValues()
    {
        var values = Enumerable.Range(0, 4)
            .Select(index => $"{index:D2}-" + new string((char)('A' + index), 247))
            .ToArray();
        var projector = CreateProjector();

        var projection = projector.ProjectSorted(
            "Programs",
            values,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, projection.TotalCount);
        Assert.Equal(1, projection.OmittedCount);
        Assert.Equal(
            string.Join("; ", values.Take(3)),
            projection.Value);
        Assert.Equal(
            754,
            projection.Value.EnumerateRunes().Count());
        Assert.DoesNotContain(values[3], projection.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void IndividuallyOversizedSecondaryValueIsOmittedAndNotLogged()
    {
        const string marker = "SENSITIVE-OMITTED-VALUE-";
        var oversized = marker + string.Concat(
            Enumerable.Repeat(
                "\U0001F600",
                ScreeningHistoryLimits.FieldValueRunes));
        var logger = new RecordingLogger<OfacBoundedFieldProjector>();
        var projector = new OfacBoundedFieldProjector(logger);

        var projection = projector.ProjectSorted(
            "Programs",
            [oversized, "SAFE"],
            TestContext.Current.CancellationToken);

        Assert.Equal("SAFE", projection.Value);
        Assert.Equal(2, projection.TotalCount);
        Assert.Equal(1, projection.OmittedCount);
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains(marker, StringComparison.Ordinal));
        Assert.Contains(
            logger.Messages,
            message => message.Contains("Programs", StringComparison.Ordinal)
                       && message.Contains("1 individually oversized", StringComparison.Ordinal));
        Assert.All(
            logger.Levels,
            level => Assert.Equal(LogLevel.Debug, level));
    }

    [Fact]
    public void NonBmpValuesAndSeparatorUseRuneBudgetWithoutSplittingValues()
    {
        var first = string.Concat(Enumerable.Repeat("\U0001F600", 499));
        var second = string.Concat(Enumerable.Repeat("\U0001F642", 499));
        var projector = CreateProjector();

        var projection = projector.ProjectSorted(
            "Nationality",
            [second, first],
            TestContext.Current.CancellationToken);

        Assert.Equal($"{first}; {second}", projection.Value);
        Assert.Equal(
            ScreeningHistoryLimits.FieldValueRunes,
            projection.Value.EnumerateRunes().Count());
        Assert.Equal(0, projection.OmittedCount);
    }

    [Fact]
    public void RepresentativeSkipsOversizedValueAndReportsEveryUnrepresentedValue()
    {
        var oversized = new string(
            'X',
            ScreeningHistoryLimits.FieldValueRunes + 1);
        var projector = CreateProjector();

        var projection = projector.ProjectRepresentative(
            "Address",
            [oversized, "Second address", "Third address"],
            TestContext.Current.CancellationToken);

        Assert.Equal("Second address", projection.Value);
        Assert.Equal(3, projection.TotalCount);
        Assert.Equal(2, projection.OmittedCount);
    }

    private static OfacBoundedFieldProjector CreateProjector() =>
        new(NullLogger<OfacBoundedFieldProjector>.Instance);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Messages =>
            _entries.Select(entry => entry.Message).ToArray();

        public IReadOnlyList<LogLevel> Levels =>
            _entries.Select(entry => entry.Level).ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel)
        {
            _ = logLevel;
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = eventId;
            _ = exception;
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
