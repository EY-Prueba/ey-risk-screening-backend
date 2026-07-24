using System.Collections.ObjectModel;
using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal sealed record OfacRecord(
    string Uid,
    string PrimaryName,
    string Type,
    string ListName,
    IReadOnlyList<string> Programs,
    IReadOnlyList<OfacAlias> Aliases,
    IReadOnlyList<OfacAddress> Addresses,
    IReadOnlyList<string> Nationalities)
{
    public static IReadOnlyList<T> AsReadOnly<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());

    public static bool HasCompatibleIdentity(OfacRecord left, OfacRecord right) =>
        string.Equals(
            EntityNameNormalizer.Normalize(left.PrimaryName).Value,
            EntityNameNormalizer.Normalize(right.PrimaryName).Value,
            StringComparison.Ordinal)
        && string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase);

    public static bool AreEquivalent(OfacRecord left, OfacRecord right) =>
        string.Equals(left.Uid, right.Uid, StringComparison.Ordinal)
        && string.Equals(left.PrimaryName, right.PrimaryName, StringComparison.Ordinal)
        && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
        && string.Equals(left.ListName, right.ListName, StringComparison.Ordinal)
        && left.Programs.SequenceEqual(right.Programs, StringComparer.Ordinal)
        && left.Aliases.SequenceEqual(right.Aliases)
        && left.Addresses.SequenceEqual(right.Addresses)
        && left.Nationalities.SequenceEqual(
            right.Nationalities,
            StringComparer.Ordinal);
}

internal sealed record OfacAlias(string Name, string Type, string Quality);

internal sealed record OfacAddress(string FormattedAddress, string Country);
