using System.Globalization;

namespace Khata.Application;

public record ManagedProfileFields(string? DisplayName, string? DateOfBirth);
public record ManagedProfile(string Id, string DisplayName, string? DateOfBirth,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Version, bool DeletionPending = false);
public record ManagedProfilePage(IReadOnlyList<ManagedProfile> Items, string? ContinuationToken);
public enum ProfileCreateStatus { Created, Replayed, Conflict, Gone }
public record ProfileCreateResult(ProfileCreateStatus Status, ManagedProfile? Profile);
public enum ProfileWriteStatus { Updated, Deleted, DeletionPending, Conflict, Missing }
public record ProfileWriteResult(ProfileWriteStatus Status, ManagedProfile? Profile = null);

public interface IManagedProfileStore
{
    Task<ProfileCreateResult> CreateAsync(string ownerId, Guid requestId, ManagedProfileFields fields, CancellationToken cancellationToken);
    Task<ManagedProfilePage> ListAsync(string ownerId, string? continuationToken, CancellationToken cancellationToken);
    Task<ManagedProfile?> GetAsync(string ownerId, string id, CancellationToken cancellationToken);
    Task<ProfileWriteResult> UpdateAsync(string ownerId, string id, ManagedProfileFields fields, string version, CancellationToken cancellationToken);
    Task<ProfileWriteResult> DeleteAsync(string ownerId, string id, string version, CancellationToken cancellationToken);
}

public sealed class InvalidProfileContinuationException : Exception
{
    public InvalidProfileContinuationException(Exception innerException) : base("Invalid continuation token.", innerException) { }
}

public static class ManagedProfileValidation
{
    public static (ManagedProfileFields Fields, Dictionary<string, string> Errors) Validate(ManagedProfileFields input, TimeProvider clock)
    {
        var name = input.DisplayName?.Trim();
        var date = string.IsNullOrWhiteSpace(input.DateOfBirth) ? null : input.DateOfBirth;
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(name) || name.Length > 100 || name.Any(char.IsControl))
            errors["displayName"] = "Enter a display name of up to 100 characters.";
        if (date is not null &&
            (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birthDate) ||
             birthDate > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)))
            errors["dateOfBirth"] = "Enter a valid date that is not in the future.";
        return (new ManagedProfileFields(name, date), errors);
    }
}
