using System.Globalization;
using System.Text;

namespace EyRiskScreening.Domain.Screening;

public static class EntityNameNormalizer
{
    public static NormalizedName Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var separatorPending = false;

        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                if (separatorPending && builder.Length > 0)
                {
                    _ = builder.Append(' ');
                }

                _ = builder.Append(Rune.ToUpperInvariant(rune));
                separatorPending = false;
                continue;
            }

            separatorPending = builder.Length > 0;
        }

        return new NormalizedName(builder.ToString().Normalize(NormalizationForm.FormC));
    }
}
