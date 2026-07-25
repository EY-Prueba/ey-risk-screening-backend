using System.Globalization;
using System.Net.Mail;
using System.Text;
using EyRiskScreening.Domain.Suppliers;

namespace EyRiskScreening.Application.Suppliers;

public static class SupplierValidator
{
    public static SupplierInputValidationResult Validate(SupplierInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<SupplierValidationError>();

        ValidateText(
            input.LegalName,
            "legalName",
            "Legal name",
            SupplierLimits.LegalNameMinimumRunes,
            SupplierLimits.LegalNameMaximumRunes,
            errors);
        ValidateText(
            input.CommercialName,
            "commercialName",
            "Commercial name",
            SupplierLimits.CommercialNameMinimumRunes,
            SupplierLimits.CommercialNameMaximumRunes,
            errors);
        ValidateTaxId(input.TaxId, errors);
        ValidatePhoneNumber(input.PhoneNumber, errors);
        ValidateEmail(input.Email, errors);
        ValidateWebsite(input.Website, errors);
        ValidateText(
            input.PhysicalAddress,
            "physicalAddress",
            "Physical address",
            SupplierLimits.PhysicalAddressMinimumRunes,
            SupplierLimits.PhysicalAddressMaximumRunes,
            errors);
        ValidateText(
            input.Country,
            "country",
            "Country",
            SupplierLimits.CountryMinimumRunes,
            SupplierLimits.CountryMaximumRunes,
            errors);
        ValidateAnnualBilling(input.AnnualBillingUsd, errors);

        return errors.Count > 0
            ? new SupplierInputValidationResult(null, errors)
            : new SupplierInputValidationResult(
                new NormalizedSupplierInput(
                    input.LegalName!.Trim(),
                    input.CommercialName!.Trim(),
                    input.TaxId!.Trim(),
                    input.PhoneNumber!.Trim(),
                    input.Email!.Trim(),
                    input.Website!.Trim(),
                    input.PhysicalAddress!.Trim(),
                    input.Country!.Trim(),
                    input.AnnualBillingUsd!.Value),
                []);
    }

    public static SupplierListValidationResult Validate(
        SupplierListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new List<SupplierValidationError>();
        if (query.Page < 1)
        {
            Add(
                errors,
                "page",
                "PageRange",
                "Page must be greater than or equal to 1.");
        }

        if (query.PageSize is < 1 or > 100)
        {
            Add(
                errors,
                "pageSize",
                "PageSizeRange",
                "Page size must contain a value between 1 and 100.");
        }

        var search = NormalizeOptionalQuery(
            query.Search,
            "search",
            200,
            errors);
        var country = NormalizeOptionalQuery(
            query.Country,
            "country",
            SupplierLimits.CountryMaximumRunes,
            errors);
        var sortBy = ParseSortBy(query.SortBy, errors);
        var direction = ParseDirection(query.SortDirection, errors);

        return errors.Count > 0
            ? new SupplierListValidationResult(null, errors)
            : new SupplierListValidationResult(
                new SupplierListCriteria(
                    query.Page,
                    query.PageSize,
                    search,
                    country,
                    sortBy,
                    direction),
                []);
    }

    private static void ValidateTaxId(
        string? value,
        List<SupplierValidationError> errors)
    {
        if (HasControlCharacter(value, "taxId", "Tax ID", errors))
        {
            return;
        }

        var trimmed = value?.Trim();
        if (trimmed is null
            || trimmed.Length != SupplierLimits.TaxIdLength
            || trimmed.Any(character => !char.IsAsciiDigit(character)))
        {
            Add(
                errors,
                "taxId",
                "TaxIdFormat",
                "Tax ID must contain exactly 11 ASCII digits.");
        }
    }

    private static void ValidatePhoneNumber(
        string? value,
        List<SupplierValidationError> errors)
    {
        if (HasControlCharacter(value, "phoneNumber", "Phone number", errors))
        {
            return;
        }

        var trimmed = value?.Trim();
        if (trimmed is null
            || trimmed.Length is < SupplierLimits.PhoneNumberMinimumLength
                or > SupplierLimits.PhoneNumberMaximumLength)
        {
            Add(
                errors,
                "phoneNumber",
                "PhoneNumberLength",
                $"Phone number must contain between {SupplierLimits.PhoneNumberMinimumLength} and {SupplierLimits.PhoneNumberMaximumLength} characters.");
            return;
        }

        var digitCount = 0;
        foreach (var character in trimmed)
        {
            if (char.IsAsciiDigit(character))
            {
                digitCount++;
            }
            else if (character is not (' ' or '+' or '-' or '(' or ')'))
            {
                Add(
                    errors,
                    "phoneNumber",
                    "PhoneNumberFormat",
                    "Phone number contains an unsupported character.");
                return;
            }
        }

        if (digitCount is < SupplierLimits.PhoneNumberMinimumDigits
            or > SupplierLimits.PhoneNumberMaximumDigits)
        {
            Add(
                errors,
                "phoneNumber",
                "PhoneNumberDigits",
                $"Phone number must contain between {SupplierLimits.PhoneNumberMinimumDigits} and {SupplierLimits.PhoneNumberMaximumDigits} digits.");
        }
    }

    private static void ValidateEmail(
        string? value,
        List<SupplierValidationError> errors)
    {
        if (HasControlCharacter(value, "email", "Email", errors))
        {
            return;
        }

        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.EnumerateRunes().Count()
                > SupplierLimits.EmailMaximumRunes
            || !MailAddress.TryCreate(trimmed, out var parsed)
            || !string.Equals(
                parsed.Address,
                trimmed,
                StringComparison.OrdinalIgnoreCase))
        {
            Add(
                errors,
                "email",
                "EmailFormat",
                "Email must be a valid address of no more than 254 characters.");
        }
    }

    private static void ValidateWebsite(
        string? value,
        List<SupplierValidationError> errors)
    {
        if (HasControlCharacter(value, "website", "Website", errors))
        {
            return;
        }

        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.EnumerateRunes().Count()
                > SupplierLimits.WebsiteMaximumRunes
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            Add(
                errors,
                "website",
                "WebsiteFormat",
                "Website must be an absolute HTTP or HTTPS URL without user information.");
        }
    }

    private static void ValidateAnnualBilling(
        decimal? value,
        List<SupplierValidationError> errors)
    {
        if (!value.HasValue)
        {
            Add(
                errors,
                "annualBillingUsd",
                "AnnualBillingRequired",
                "Annual billing in USD is required.");
            return;
        }

        if (value.Value < 0
            || value.Value > SupplierLimits.AnnualBillingMaximumUsd)
        {
            Add(
                errors,
                "annualBillingUsd",
                "AnnualBillingRange",
                $"Annual billing in USD must be between 0 and {SupplierLimits.AnnualBillingMaximumUsd.ToString(CultureInfo.InvariantCulture)}.");
            return;
        }

        if (decimal.Round(value.Value, 2) != value.Value)
        {
            Add(
                errors,
                "annualBillingUsd",
                "AnnualBillingScale",
                "Annual billing in USD must contain no more than two decimal places.");
        }
    }

    private static void ValidateText(
        string? value,
        string field,
        string label,
        int minimumRunes,
        int maximumRunes,
        List<SupplierValidationError> errors)
    {
        if (HasControlCharacter(value, field, label, errors))
        {
            return;
        }

        var trimmed = value?.Trim();
        var runeCount = trimmed?.EnumerateRunes().Count() ?? 0;
        if (runeCount < minimumRunes || runeCount > maximumRunes)
        {
            Add(
                errors,
                field,
                $"{field}Length",
                $"{label} must contain between {minimumRunes} and {maximumRunes} Unicode characters.");
        }
    }

    private static bool HasControlCharacter(
        string? value,
        string field,
        string label,
        List<SupplierValidationError> errors)
    {
        if (value is null)
        {
            Add(errors, field, $"{field}Required", $"{label} is required.");
            return true;
        }

        if (value.EnumerateRunes().Any(
                rune => Rune.GetUnicodeCategory(rune)
                    == UnicodeCategory.Control))
        {
            Add(
                errors,
                field,
                $"{field}ControlCharacter",
                $"{label} must not contain control characters.");
            return true;
        }

        return false;
    }

    private static string? NormalizeOptionalQuery(
        string? value,
        string field,
        int maximumRunes,
        List<SupplierValidationError> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (value.EnumerateRunes().Any(
                rune => Rune.GetUnicodeCategory(rune)
                    == UnicodeCategory.Control))
        {
            Add(
                errors,
                field,
                $"{field}ControlCharacter",
                $"{field} must not contain control characters.");
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.EnumerateRunes().Count() > maximumRunes)
        {
            Add(
                errors,
                field,
                $"{field}Length",
                $"{field} must not exceed {maximumRunes} Unicode characters.");
        }

        return trimmed;
    }

    private static SupplierSortBy ParseSortBy(
        string? value,
        List<SupplierValidationError> errors)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)
            || string.Equals(
                normalized,
                "lastEditedAtUtc",
                StringComparison.OrdinalIgnoreCase))
        {
            return SupplierSortBy.LastEditedAtUtc;
        }

        var result = normalized.ToLowerInvariant() switch
        {
            "legalname" => SupplierSortBy.LegalName,
            "commercialname" => SupplierSortBy.CommercialName,
            "taxid" => SupplierSortBy.TaxId,
            "country" => SupplierSortBy.Country,
            "annualbillingusd" => SupplierSortBy.AnnualBillingUsd,
            _ => (SupplierSortBy?)null,
        };
        if (result.HasValue)
        {
            return result.Value;
        }

        Add(
            errors,
            "sortBy",
            "SortByInvalid",
            "Sort by must be one of: lastEditedAtUtc, legalName, commercialName, taxId, country, annualBillingUsd.");
        return SupplierSortBy.LastEditedAtUtc;
    }

    private static SupplierSortDirection ParseDirection(
        string? value,
        List<SupplierValidationError> errors)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)
            || string.Equals(
                normalized,
                "desc",
                StringComparison.OrdinalIgnoreCase))
        {
            return SupplierSortDirection.Desc;
        }

        if (string.Equals(normalized, "asc", StringComparison.OrdinalIgnoreCase))
        {
            return SupplierSortDirection.Asc;
        }

        Add(
            errors,
            "sortDirection",
            "SortDirectionInvalid",
            "Sort direction must be either asc or desc.");
        return SupplierSortDirection.Desc;
    }

    private static void Add(
        List<SupplierValidationError> errors,
        string field,
        string code,
        string message) =>
        errors.Add(new SupplierValidationError(field, code, message));
}

public sealed record NormalizedSupplierInput(
    string LegalName,
    string CommercialName,
    string TaxId,
    string PhoneNumber,
    string Email,
    string Website,
    string PhysicalAddress,
    string Country,
    decimal AnnualBillingUsd);

public sealed record SupplierInputValidationResult(
    NormalizedSupplierInput? Input,
    IReadOnlyList<SupplierValidationError> Errors);

public sealed record SupplierListValidationResult(
    SupplierListCriteria? Criteria,
    IReadOnlyList<SupplierValidationError> Errors);
