using System.Globalization;
using System.Text;
using EyRiskScreening.Domain.Screening;

namespace EyRiskScreening.Application.Screening;

public static class ScreeningRequestValidator
{
    private const int MinimumNameLength = 2;
    private const int MaximumNameLength = 200;
    private const int MaximumSources = 3;

    public static IReadOnlyList<ScreeningValidationError> Validate(ScreeningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<ScreeningValidationError>();
        ValidateEntityName(request.EntityName, errors);
        ValidateSources(request.Sources, errors);
        return errors;
    }

    private static void ValidateEntityName(
        string? entityName,
        List<ScreeningValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            errors.Add(new ScreeningValidationError(
                "entityName",
                "EntityNameRequired",
                "Entity name is required."));
            return;
        }

        if (entityName.EnumerateRunes().Any(
                rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control))
        {
            errors.Add(new ScreeningValidationError(
                "entityName",
                "EntityNameControlCharacter",
                "Entity name must not contain control characters."));
        }

        var trimmedName = entityName.Trim();
        var runeCount = trimmedName.EnumerateRunes().Count();
        if (runeCount is < MinimumNameLength or > MaximumNameLength)
        {
            errors.Add(new ScreeningValidationError(
                "entityName",
                "EntityNameLength",
                $"Entity name must contain between {MinimumNameLength} and {MaximumNameLength} Unicode characters."));
        }

        if (EntityNameNormalizer.Normalize(trimmedName).Value.Length == 0)
        {
            errors.Add(new ScreeningValidationError(
                "entityName",
                "EntityNameNotSearchable",
                "Entity name must contain at least one Unicode letter or number."));
        }
    }

    private static void ValidateSources(
        IReadOnlyList<ScreeningSource>? sources,
        List<ScreeningValidationError> errors)
    {
        if (sources is null || sources.Count == 0)
        {
            errors.Add(new ScreeningValidationError(
                "sources",
                "SourcesRequired",
                "At least one screening source is required."));
            return;
        }

        if (sources.Count > MaximumSources)
        {
            errors.Add(new ScreeningValidationError(
                "sources",
                "SourcesMaximum",
                $"No more than {MaximumSources} screening sources are allowed."));
        }

        if (sources.Any(source => !Enum.IsDefined(source)))
        {
            errors.Add(new ScreeningValidationError(
                "sources",
                "SourceInvalid",
                "One or more screening sources are invalid."));
        }

        if (sources.Distinct().Count() != sources.Count)
        {
            errors.Add(new ScreeningValidationError(
                "sources",
                "SourcesDuplicated",
                "Screening sources must not contain duplicates."));
        }
    }
}
