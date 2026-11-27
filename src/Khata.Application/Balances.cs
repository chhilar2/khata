namespace Khata.Application;

public sealed record Balance(string Id, string ProfileId, string? Currency, decimal Cash, string Version);
public enum BalanceUpdateStatus { Updated, Missing, Conflict }
public sealed record BalanceUpdateResult(BalanceUpdateStatus Status, Balance? Balance = null);
public sealed record LedgerEntry(string Id, string BalanceId, string Kind, decimal Amount,
    decimal CashAfter, DateTimeOffset CreatedAt);
public sealed record LedgerPage(IReadOnlyList<LedgerEntry> Items, string? ContinuationToken);
public enum LedgerWriteStatus { Created, Replayed, Conflict, Missing, InsufficientFunds, LimitExceeded, ReceiptLimitReached }
public sealed record LedgerWriteResult(LedgerWriteStatus Status, LedgerEntry? Entry = null);
public sealed class InvalidBalanceContinuationException(Exception innerException)
    : Exception("Invalid continuation token.", innerException);

public sealed record Investment(
    string Id, string BalanceId, InvestmentKind Kind, InvestmentState State,
    decimal AnnualRatePercent, CompoundingModel Compounding, decimal Principal,
    DateOnly? FundedOn, DateOnly? ClosedOn, decimal? ClosingValue, string Version);

public sealed record InvestmentPage(IReadOnlyList<Investment> Items, string? ContinuationToken);

public enum InvestmentWriteStatus { Created, Missing, Conflict, ValidationFailed, MarketLinkedUnavailable, ReceiptLimitReached }
public sealed record InvestmentWriteResult(InvestmentWriteStatus Status, Investment? Investment = null);

public sealed record Gig(
    string Id, string BalanceId, string Name, string? Description, DateOnly StartDate,
    decimal Payout, GigState State, decimal? CompletionAmount, DateTimeOffset? CompletedAt,
    string Version);

public sealed record GigPage(IReadOnlyList<Gig> Items, string? ContinuationToken);

public enum GigWriteStatus { Created, Updated, Replayed, Missing, Conflict, ValidationFailed, ReceiptLimitReached }
public sealed record GigWriteResult(GigWriteStatus Status, Gig? Gig = null);

public enum TransitionStatus { Updated, Replayed, Missing, Conflict, ValidationFailed,
    InsufficientFunds, LimitExceeded, ValueUnavailable, ReceiptLimitReached }
public sealed record TransitionResult(TransitionStatus Status, decimal Cash, string BalanceVersion,
    Investment? Source = null, Investment? Destination = null, LedgerEntry? Entry = null);

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
    Task<InvestmentPage?> ListInvestmentsAsync(string ownerId, string balanceId, string? continuationToken, CancellationToken cancellationToken);
    Task<Investment?> GetInvestmentAsync(string ownerId, string balanceId, string investmentId, CancellationToken cancellationToken);
    Task<InvestmentWriteResult> CreateInvestmentAsync(string ownerId, string balanceId, Guid requestId,
        InvestmentKind kind, decimal annualRatePercent, CompoundingModel compounding,
        string? marketReference, string version, CancellationToken cancellationToken);
    Task<GigPage?> ListGigsAsync(string ownerId, string balanceId, string? continuationToken, CancellationToken cancellationToken);
    Task<Gig?> GetGigAsync(string ownerId, string balanceId, string gigId, CancellationToken cancellationToken);
    Task<GigWriteResult> CreateGigAsync(string ownerId, string balanceId, Guid requestId,
        string name, string? description, DateOnly startDate, decimal payout, GigState state,
        string version, CancellationToken cancellationToken);
    Task<GigWriteResult> UpdateGigAsync(string ownerId, string balanceId, string gigId, Guid requestId,
        string name, string? description, DateOnly startDate, decimal payout, GigState state,
        string version, CancellationToken cancellationToken);
    Task<TransitionResult> TransitionAsync(string ownerId, string balanceId, Guid requestId,
        string source, string destination, decimal amount, string sourceVersion, string destinationVersion,
        string balanceVersion, CancellationToken cancellationToken);
}
