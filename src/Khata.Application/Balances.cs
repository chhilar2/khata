namespace Khata.Application;

public sealed record Balance(string Id, string ProfileId, string? Currency, decimal Cash, string Version);
public enum BalanceUpdateStatus { Updated, Missing, Conflict }
public sealed record BalanceUpdateResult(BalanceUpdateStatus Status, Balance? Balance = null);
public sealed record LedgerEntry(string Id, string BalanceId, string Kind, decimal Amount,
    decimal CashAfter, DateTimeOffset CreatedAt);
public sealed record LedgerPage(IReadOnlyList<LedgerEntry> Items, string? ContinuationToken);
public enum LedgerWriteStatus { Created, Replayed, Conflict, Missing, InsufficientFunds, LimitExceeded }
public sealed record LedgerWriteResult(LedgerWriteStatus Status, LedgerEntry? Entry = null);
public sealed class InvalidBalanceContinuationException(Exception innerException)
    : Exception("Invalid continuation token.", innerException);

public interface IBalanceStore
{
    Task<Balance?> GetOrCreateForProfileAsync(string ownerId, string profileId, CancellationToken cancellationToken);
    Task<Balance?> GetAsync(string ownerId, string balanceId, CancellationToken cancellationToken);
    Task<BalanceUpdateResult> SetCurrencyAsync(string ownerId, string balanceId, string currency,
        string version, CancellationToken cancellationToken);
    Task<LedgerPage?> ListLedgerAsync(string ownerId, string balanceId, string? continuationToken, CancellationToken cancellationToken);
    Task<LedgerEntry?> GetLedgerEntryAsync(string ownerId, string balanceId, string entryId, CancellationToken cancellationToken);
    Task<LedgerWriteResult> AdjustCashAsync(string ownerId, string balanceId, Guid requestId, string operation,
        decimal amount, string version, CancellationToken cancellationToken);
}
