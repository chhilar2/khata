using System.Globalization;
using System.Net.Mail;

namespace Khata.Application;

public record AccountFields(string? DisplayName, string? ContactEmail, string? Phone, string? Country, string? Currency);
public record AccountProfile(string UserId, string Provider, string? Email, AccountFields Fields,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public record VersionedAccount(AccountProfile Profile, string Version);
public enum AccountUpdateResult { Updated, Conflict, Missing }

public interface IAccountStore
{
    Task EnsureProfileAsync(string userId, ValidatedIdentity identity, CancellationToken cancellationToken);
    Task<VersionedAccount?> GetAsync(string userId, CancellationToken cancellationToken);
    Task<AccountUpdateResult> UpdateAsync(string userId, AccountFields fields, string version, CancellationToken cancellationToken);
}

public static class AccountValidation
{
    private static readonly RegionInfo[] Regions = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(culture => new RegionInfo(culture.Name)).ToArray();

    public static (AccountFields Fields, Dictionary<string, string> Errors) Validate(AccountFields fields)
    {
        var normalized = new AccountFields(
            Clean(fields.DisplayName), Clean(fields.ContactEmail), Clean(fields.Phone),
            Clean(fields.Country)?.ToUpperInvariant(), Clean(fields.Currency)?.ToUpperInvariant());
        var errors = new Dictionary<string, string>();
        CheckText("displayName", normalized.DisplayName, 100, errors);
        CheckText("contactEmail", normalized.ContactEmail, 254, errors);
        CheckText("phone", normalized.Phone, 30, errors);
        if (normalized.ContactEmail is { } email &&
            (!MailAddress.TryCreate(email, out var address) || address.Address != email || address.DisplayName.Length != 0))
            errors["contactEmail"] = "Enter a valid email address.";
        if (normalized.Phone is { } phone &&
            (phone.Count(char.IsAsciiDigit) < 4 || phone.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or '-' or ' ' or '(' or ')' or '.'))))
            errors["phone"] = "Enter a valid phone number.";
        if (normalized.Country is { } country &&
            (country.Length != 2 || !Regions.Any(region => region.TwoLetterISORegionName == country)))
            errors["country"] = "Enter a supported two-letter country code.";
        if (normalized.Currency is { } currency &&
            (currency.Length != 3 || !Regions.Any(region => region.ISOCurrencySymbol == currency)))
            errors["currency"] = "Enter a supported three-letter currency code.";
        return (normalized, errors);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void CheckText(string name, string? value, int maxLength, Dictionary<string, string> errors)
    {
        if (value is not null && (value.Length > maxLength || value.Any(char.IsControl)))
            errors[name] = $"Use at most {maxLength} characters without control characters.";
    }
}
