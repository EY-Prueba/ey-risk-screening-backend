using System.Text;
using System.Text.Json;
using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening.History;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class ScreeningSourceCandidatePersistenceGuardTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void ValidUnicodeCandidateIsAccepted()
    {
        ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
            "reference-\U0001F600",
            "Soci\u00E9t\u00E9 \U0001F600",
            [new ScreeningSourceField("Country", "Per\u00FA \U0001F600")]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void EmptyOrUnsearchableNameIsRejected(string name)
    {
        Assert.Throws<ScreeningHistoryValidationException>(() =>
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                "reference",
                name,
                []));
    }

    [Fact]
    public void NameUsesRuneLimit()
    {
        var exact = new string(
            'A',
            ScreeningHistoryLimits.MatchNameRunes);
        var exceeded = $"{exact}A";

        ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
            "reference",
            exact,
            []);
        Assert.Throws<ScreeningHistoryValidationException>(() =>
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                "reference",
                exceeded,
                []));
    }

    [Fact]
    public void ReferenceIdIsRequiredAndUsesRuneLimit()
    {
        var exact = new string(
            'R',
            ScreeningHistoryLimits.ReferenceIdRunes);

        ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
            exact,
            "Acme",
            []);
        Assert.Throws<ScreeningHistoryValidationException>(() =>
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                string.Empty,
                "Acme",
                []));
        Assert.Throws<ScreeningHistoryValidationException>(() =>
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                $"{exact}R",
                "Acme",
                []));
    }

    [Fact]
    public void FieldCountAndNamesUseExactLimits()
    {
        var fields = Enumerable.Range(
                0,
                ScreeningHistoryLimits.MaximumFieldsPerMatch)
            .Select(index => new ScreeningSourceField(
                new string(
                    (char)('A' + (index % 26)),
                    ScreeningHistoryLimits.FieldNameRunes),
                "value"))
            .ToArray();

        ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
            "reference",
            "Acme",
            fields);
        Assert.Throws<ScreeningHistoryValidationException>(() =>
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                "reference",
                "Acme",
                [.. fields, new ScreeningSourceField("Extra", "value")]));
        Assert.Throws<ScreeningHistoryValidationException>(() =>
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                "reference",
                "Acme",
                [new ScreeningSourceField(string.Empty, "value")]));
        Assert.Throws<ScreeningHistoryValidationException>(() =>
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                "reference",
                "Acme",
                [
                    new ScreeningSourceField(
                        new string(
                            'K',
                            ScreeningHistoryLimits.FieldNameRunes + 1),
                        "value"),
                ]));
    }

    [Fact]
    public void FieldValueUsesRuneLimitAndRejectsControls()
    {
        var rune = char.ConvertFromUtf32(0x1F600);
        var exact = string.Concat(
            Enumerable.Repeat(
                rune,
                ScreeningHistoryLimits.FieldValueRunes));

        ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
            "reference",
            "Acme",
            [new ScreeningSourceField("Value", exact)]);
        Assert.Throws<ScreeningHistoryValidationException>(() =>
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                "reference",
                "Acme",
                [
                    new ScreeningSourceField(
                        "Value",
                        string.Concat(exact, rune)),
                ]));
        Assert.Throws<ScreeningHistoryValidationException>(() =>
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                "reference",
                "Acme",
                [new ScreeningSourceField("Value", "line\nbreak")]));
    }

    [Fact]
    public void SerializedJsonAtLimitIsAcceptedAndPastLimitIsRejected()
    {
        var exact = CreateFieldsWithSerializedByteCount(
            ScreeningHistoryLimits.MaximumFieldsJsonBytes);

        ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
            "reference",
            "Acme",
            exact);

        var first = exact[0];
        var exceeded = exact.ToArray();
        exceeded[0] = first with { Value = $"{first.Value}\u00E9" };
        Assert.Throws<ScreeningHistoryValidationException>(() =>
            ScreeningSourceCandidatePersistenceGuard.ValidateMatch(
                "reference",
                "Acme",
                exceeded));
    }

    private static ScreeningSourceField[] CreateFieldsWithSerializedByteCount(
        int expectedBytes)
    {
        const int fieldCount = ScreeningHistoryLimits.MaximumFieldsPerMatch;
        const int runesPerField = ScreeningHistoryLimits.FieldValueRunes;
        var values = Enumerable.Range(0, fieldCount)
            .Select(_ => new char[runesPerField])
            .ToArray();
        foreach (var value in values)
        {
            Array.Fill(value, 'a');
        }

        var fields = CreateFields(values);
        var currentBytes = SerializedBytes(fields);
        var missingBytes = expectedBytes - currentBytes;
        Assert.True(missingBytes > 0 && missingBytes % 2 == 0);
        values[0][0] = '\u00E9';
        var escapedUnicodeDelta = SerializedBytes(CreateFields(values))
            - currentBytes;
        values[0][0] = 'a';
        values[0][0] = '\\';
        var escapedSlashDelta = SerializedBytes(CreateFields(values))
            - currentBytes;
        values[0][0] = 'a';
        Assert.True(escapedUnicodeDelta > 0);
        Assert.True(escapedSlashDelta > 0);

        var escapedUnicodeCount = -1;
        var escapedSlashCount = -1;
        for (var unicodeCount = 0;
             unicodeCount <= values.Length * runesPerField;
             unicodeCount++)
        {
            var remainder =
                missingBytes - (unicodeCount * escapedUnicodeDelta);
            if (remainder < 0)
            {
                break;
            }

            if (remainder % escapedSlashDelta == 0
                && unicodeCount + (remainder / escapedSlashDelta)
                    <= values.Length * runesPerField)
            {
                escapedUnicodeCount = unicodeCount;
                escapedSlashCount = remainder / escapedSlashDelta;
                break;
            }
        }

        Assert.True(escapedUnicodeCount >= 0 && escapedSlashCount >= 0);
        for (var index = 0;
             index < escapedUnicodeCount + escapedSlashCount;
             index++)
        {
            var field = index / runesPerField;
            var position = index % runesPerField;
            values[field][position] =
                index < escapedUnicodeCount ? '\u00E9' : '\\';
        }

        fields = CreateFields(values);
        currentBytes = SerializedBytes(fields);
        Assert.Equal(expectedBytes, currentBytes);
        return fields;
    }

    private static ScreeningSourceField[] CreateFields(char[][] values) =>
        values.Select((value, index) =>
                new ScreeningSourceField($"F{index:D2}", new string(value)))
            .ToArray();

    private static int SerializedBytes(
        IReadOnlyList<ScreeningSourceField> fields)
    {
        var json = JsonSerializer.Serialize(
            fields.Select(field => new StoredField(field.Name, field.Value)),
            JsonOptions);
        return Encoding.Unicode.GetByteCount(json);
    }

    private sealed record StoredField(string Name, string Value);
}
