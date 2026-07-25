using EyRiskScreening.Application.Screening;
using EyRiskScreening.Domain.Screening;
using Xunit;

namespace EyRiskScreening.UnitTests;

public sealed class ScreeningRequestValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingEntityNameIsRejected(string? entityName)
    {
        var errors = ScreeningRequestValidator.Validate(new ScreeningRequest(
            entityName,
            [ScreeningSource.Ofac]));

        Assert.Contains(errors, error => error.Code == "EntityNameRequired");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("---")]
    public void UnsearchableOrShortEntityNameIsRejected(string entityName)
    {
        var errors = ScreeningRequestValidator.Validate(new ScreeningRequest(
            entityName,
            [ScreeningSource.Ofac]));

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void EntityNameLongerThanTwoHundredUnicodeScalarsIsRejected()
    {
        var errors = ScreeningRequestValidator.Validate(new ScreeningRequest(
            new string('A', 201),
            [ScreeningSource.Ofac]));

        Assert.Contains(errors, error => error.Code == "EntityNameLength");
    }

    [Theory]
    [InlineData("Acme\n")]
    [InlineData("\tAcme")]
    [InlineData("Ac\tme")]
    [InlineData("Acme\u0000Corporation")]
    public void ControlCharactersAreRejectedRegardlessOfPosition(string entityName)
    {
        var errors = ScreeningRequestValidator.Validate(new ScreeningRequest(
            entityName,
            [ScreeningSource.Ofac]));

        Assert.Contains(errors, error => error.Code == "EntityNameControlCharacter");
    }

    [Theory]
    [InlineData(" Acme ")]
    [InlineData("\u00A0Acme\u00A0")]
    [InlineData("Acme\u2003Corporation")]
    public void NonControlUnicodeSpacesFollowExistingValidationRules(string entityName)
    {
        var errors = ScreeningRequestValidator.Validate(new ScreeningRequest(
            entityName,
            [ScreeningSource.Ofac]));

        Assert.Empty(errors);
    }

    [Theory]
    [MemberData(nameof(ValidSources))]
    public void OneOrThreeKnownSourcesAreAccepted(ScreeningSource[] sources)
    {
        var errors = ScreeningRequestValidator.Validate(new ScreeningRequest(
            "Acme Corporation",
            sources));

        Assert.Empty(errors);
    }

    public static TheoryData<ScreeningSource[]> ValidSources => new()
    {
        { new[] { ScreeningSource.Ofac } },
        { Enum.GetValues<ScreeningSource>() },
    };

    [Fact]
    public void MissingSourcesAreRejected()
    {
        var errors = ScreeningRequestValidator.Validate(new ScreeningRequest(
            "Acme Corporation",
            []));

        Assert.Contains(errors, error => error.Code == "SourcesRequired");
    }

    [Fact]
    public void MoreThanThreeSourcesAreRejected()
    {
        var errors = ScreeningRequestValidator.Validate(new ScreeningRequest(
            "Acme Corporation",
            [
                ScreeningSource.OffshoreLeaks,
                ScreeningSource.WorldBank,
                ScreeningSource.Ofac,
                ScreeningSource.Ofac,
            ]));

        Assert.Contains(errors, error => error.Code == "SourcesMaximum");
    }

    [Fact]
    public void DuplicateSourcesAreRejected()
    {
        var errors = ScreeningRequestValidator.Validate(new ScreeningRequest(
            "Acme Corporation",
            [ScreeningSource.Ofac, ScreeningSource.Ofac]));

        Assert.Contains(errors, error => error.Code == "SourcesDuplicated");
    }

    [Fact]
    public void UnknownSourceIsRejected()
    {
        var errors = ScreeningRequestValidator.Validate(new ScreeningRequest(
            "Acme Corporation",
            [(ScreeningSource)999]));

        Assert.Contains(errors, error => error.Code == "SourceInvalid");
    }
}
