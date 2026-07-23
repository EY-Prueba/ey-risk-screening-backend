using System.Globalization;
using System.Text;

namespace EyRiskScreening.Domain.Screening.History;

public static class ScreeningHistoryLimits
{
    // SQL Server nvarchar lengths are UTF-16 code units; one Unicode rune can
    // require a surrogate pair, so persisted capacity is twice the rune limit.
    public const int Utf16CodeUnitsPerRune = 2;
    public const int EntityNameRunes = 400;
    public const int ReferenceIdRunes = 512;
    public const int MatchNameRunes = 1000;
    public const int ErrorMessageRunes = 1000;
    public const int FieldNameRunes = 100;
    public const int FieldValueRunes = 1000;
    public const int MaximumFieldsPerMatch = 32;
    public const int MaximumMatchesPerSource = 1000;
    public const int MaximumFieldsJsonBytes = 131072;
    public const int EntityNameSqlLength =
        EntityNameRunes * Utf16CodeUnitsPerRune;
    public const int ReferenceIdSqlLength =
        ReferenceIdRunes * Utf16CodeUnitsPerRune;
    public const int MatchNameSqlLength =
        MatchNameRunes * Utf16CodeUnitsPerRune;
    public const int ErrorMessageSqlLength =
        ErrorMessageRunes * Utf16CodeUnitsPerRune;
}

internal static class ScreeningHistoryGuard
{
    public static void RequiredText(string value, int maximumRunes, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ScreeningHistoryValidationException(
                $"{parameterName} must not be empty.");
        }

        if (value.EnumerateRunes().Any(rune =>
                Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control))
        {
            throw new ScreeningHistoryValidationException(
                $"{parameterName} must not contain control characters.");
        }

        if (value.EnumerateRunes().Count() > maximumRunes)
        {
            throw new ScreeningHistoryValidationException(
                $"{parameterName} exceeds its maximum length.");
        }
    }

    public static void DefinedEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ScreeningHistoryValidationException(
                $"{parameterName} is not a defined value.");
        }
    }

    public static void Score(decimal value, string parameterName)
    {
        if (value is < 0 or > 100)
        {
            throw new ScreeningHistoryValidationException(
                $"{parameterName} must be between 0 and 100.");
        }
    }
}
