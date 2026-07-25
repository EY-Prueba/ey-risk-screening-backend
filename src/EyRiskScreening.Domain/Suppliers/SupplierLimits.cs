namespace EyRiskScreening.Domain.Suppliers;

public static class SupplierLimits
{
    public const int LegalNameMinimumRunes = 2;
    public const int LegalNameMaximumRunes = 200;
    public const int CommercialNameMinimumRunes = 2;
    public const int CommercialNameMaximumRunes = 200;
    public const int TaxIdLength = 11;
    public const int PhoneNumberMinimumLength = 7;
    public const int PhoneNumberMaximumLength = 30;
    public const int PhoneNumberMinimumDigits = 7;
    public const int PhoneNumberMaximumDigits = 15;
    public const int EmailMaximumRunes = 254;
    public const int WebsiteMaximumRunes = 2048;
    public const int PhysicalAddressMinimumRunes = 2;
    public const int PhysicalAddressMaximumRunes = 500;
    public const int CountryMinimumRunes = 2;
    public const int CountryMaximumRunes = 100;
    public const decimal AnnualBillingMaximumUsd = 9999999999999999.99m;
}
