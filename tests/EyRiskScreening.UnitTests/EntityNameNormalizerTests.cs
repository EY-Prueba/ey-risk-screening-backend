using EyRiskScreening.Domain.Screening;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class EntityNameNormalizerTests
{
    [Theory]
    [InlineData("Acme Corporation", "ACME CORPORATION")]
    [InlineData("ÁÇMÉ Córporation", "ACME CORPORATION")]
    [InlineData("  Acme...Corporation  ", "ACME CORPORATION")]
    [InlineData("O’Connor-Ltd.", "O CONNOR LTD")]
    [InlineData("AT&T   Holdings", "AT T HOLDINGS")]
    [InlineData("東京  商事", "東京 商事")]
    [InlineData("ООО Ромашка", "ООО РОМАШКА")]
    public void NormalizeProducesExpectedUnicodeResult(string value, string expected)
    {
        Assert.Equal(expected, EntityNameNormalizer.Normalize(value).Value);
    }

    [Fact]
    public void NormalizeIsIdempotent()
    {
        var first = EntityNameNormalizer.Normalize("  Société—東京  ");
        var second = EntityNameNormalizer.Normalize(first.Value);

        Assert.Equal(first, second);
    }

    [Fact]
    public void NormalizeDoesNotTransliterateNonLatinLetters()
    {
        var normalized = EntityNameNormalizer.Normalize("東京");

        Assert.Equal("東京", normalized.Value);
    }
}
