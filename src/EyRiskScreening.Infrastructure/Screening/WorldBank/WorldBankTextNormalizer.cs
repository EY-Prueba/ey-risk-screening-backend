using System.Globalization;
using System.Text;

namespace EyRiskScreening.Infrastructure.Screening.WorldBank;

internal static class WorldBankTextNormalizer
{
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var whitespacePending = false;

        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control)
            {
                throw new WorldBankAdapterException(
                    "World Bank content contains a control character.");
            }

            if (Rune.IsWhiteSpace(rune))
            {
                whitespacePending = builder.Length > 0;
                continue;
            }

            if (whitespacePending)
            {
                _ = builder.Append(' ');
                whitespacePending = false;
            }

            _ = builder.Append(rune);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
