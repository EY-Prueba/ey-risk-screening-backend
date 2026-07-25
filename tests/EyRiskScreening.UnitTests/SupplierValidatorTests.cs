using EyRiskScreening.Application.Suppliers;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class SupplierValidatorTests
{
    [Fact]
    public void ValidInputIsTrimmedWithoutChangingInternalContent()
    {
        var result = SupplierValidator.Validate(ValidInput() with
        {
            LegalName = "  Société & Co., S.A.  ",
            CommercialName = "  Société (Perú)  ",
            TaxId = " 00123456789 ",
        });

        Assert.Empty(result.Errors);
        Assert.Equal("Société & Co., S.A.", result.Input?.LegalName);
        Assert.Equal("Société (Perú)", result.Input?.CommercialName);
        Assert.Equal("00123456789", result.Input?.TaxId);
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("123456789012")]
    [InlineData("1234567890A")]
    [InlineData("12345 67890")]
    public void InvalidTaxIdIsRejected(string taxId)
    {
        var result = SupplierValidator.Validate(
            ValidInput() with { TaxId = taxId });

        Assert.Contains(result.Errors, error => error.Field == "taxId");
    }

    [Theory]
    [InlineData("", "legalName")]
    [InlineData(" ", "commercialName")]
    [InlineData("A", "legalName")]
    public void InvalidNamesAreRejected(string value, string field)
    {
        var input = field == "legalName"
            ? ValidInput() with { LegalName = value }
            : ValidInput() with { CommercialName = value };

        Assert.Contains(
            SupplierValidator.Validate(input).Errors,
            error => error.Field == field);
    }

    [Theory]
    [InlineData("Acme\nCorp", "legalName")]
    [InlineData("Peru\t", "country")]
    public void ControlCharactersAreRejectedBeforeTrim(
        string value,
        string field)
    {
        var input = field == "legalName"
            ? ValidInput() with { LegalName = value }
            : ValidInput() with { Country = value };

        Assert.Contains(
            SupplierValidator.Validate(input).Errors,
            error => error.Field == field
                && error.Code.Contains(
                    "ControlCharacter",
                    StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("phone text")]
    [InlineData("+51 123")]
    [InlineData("+51 999_999_999")]
    public void InvalidPhoneNumberIsRejected(string phoneNumber)
    {
        Assert.Contains(
            SupplierValidator.Validate(
                ValidInput() with { PhoneNumber = phoneNumber }).Errors,
            error => error.Field == "phoneNumber");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("John <john@example.com>")]
    public void InvalidEmailIsRejected(string email)
    {
        Assert.Contains(
            SupplierValidator.Validate(
                ValidInput() with { Email = email }).Errors,
            error => error.Field == "email");
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:password@example.com")]
    public void InvalidWebsiteIsRejected(string website)
    {
        Assert.Contains(
            SupplierValidator.Validate(
                ValidInput() with { Website = website }).Errors,
            error => error.Field == "website");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.001")]
    [InlineData("10000000000000000")]
    public void InvalidAnnualBillingIsRejected(string value)
    {
        Assert.Contains(
            SupplierValidator.Validate(ValidInput() with
            {
                AnnualBillingUsd = decimal.Parse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture),
            }).Errors,
            error => error.Field == "annualBillingUsd");
    }

    [Theory]
    [InlineData(0, 10, "lastEditedAtUtc", "desc", "page")]
    [InlineData(1, 0, "lastEditedAtUtc", "desc", "pageSize")]
    [InlineData(1, 101, "lastEditedAtUtc", "desc", "pageSize")]
    [InlineData(1, 10, "drop table", "desc", "sortBy")]
    [InlineData(1, 10, "legalName", "sideways", "sortDirection")]
    public void InvalidListQueryIsRejected(
        int page,
        int pageSize,
        string sortBy,
        string direction,
        string field)
    {
        var result = SupplierValidator.Validate(new SupplierListQuery(
            page,
            pageSize,
            null,
            null,
            sortBy,
            direction));

        Assert.Contains(result.Errors, error => error.Field == field);
    }

    [Theory]
    [InlineData("lastEditedAtUtc", "desc")]
    [InlineData("legalName", "asc")]
    [InlineData("commercialName", "desc")]
    [InlineData("taxId", "asc")]
    [InlineData("country", "desc")]
    [InlineData("annualBillingUsd", "asc")]
    public void SupportedSortValuesAreAccepted(
        string sortBy,
        string direction)
    {
        var result = SupplierValidator.Validate(new SupplierListQuery(
            SortBy: sortBy,
            SortDirection: direction));

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Criteria);
    }

    internal static SupplierInput ValidInput() =>
        new(
            "PARS TABLEAU COMPANY",
            "Pars Tableau",
            "20123456789",
            "+51 999 999 999",
            "contact@example.com",
            "https://example.com",
            "Av. Ejemplo 123",
            "Peru",
            1250000.50m);
}
