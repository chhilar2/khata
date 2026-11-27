using System.Net;
using Khata.Application;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using BalanceDocument = Khata.Infrastructure.CosmosManagedProfileStore.BalanceDocument;
using ProfileDocument = Khata.Infrastructure.CosmosManagedProfileStore.Document;

namespace Khata.Infrastructure;

public sealed class CosmosBalanceStore(Container container, TimeProvider clock) : IBalanceStore
{
    internal sealed class LedgerDocument
    {
        public required string Id { get; set; }
        public required string OwnerId { get; set; }
        public required string ProfileId { get; set; }
        public required string BalanceId { get; set; }
        public string Kind { get; set; } = "balance-ledger";
        public required string Operation { get; set; }
        public long AmountCents { get; set; }
        public long CashAfterCents { get; set; }
        public string? SourceId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    internal sealed class FinancialReceipt
    {
        public required string Id { get; set; }
        public required string OwnerId { get; set; }
        public required string ProfileId { get; set; }
        public required string BalanceId { get; set; }
        public string Kind { get; set; } = "financial-receipt";
        public required string Fingerprint { get; set; }
        public required LedgerDocument Entry { get; set; }
    }

    internal sealed class InvestmentDocument
    {
        public required string Id { get; set; }
        public required string OwnerId { get; set; }
        public required string ProfileId { get; set; }
        public required string BalanceId { get; set; }
        public string Kind { get; set; } = "investment";
        public string InvestmentKind { get; set; } = "fixed-rate";
        public string State { get; set; } = "unfunded";
        public decimal AnnualRatePercent { get; set; }
        public string Compounding { get; set; } = "annually";
        public long PrincipalCents { get; set; }
        public DateOnly? FundedOn { get; set; }
        public DateOnly? ClosedOn { get; set; }
        public long? ClosingValueCents { get; set; }
        public string? MarketReference { get; set; }
        [JsonProperty("_etag", NullValueHandling = NullValueHandling.Ignore)]
        public string? ETag { get; set; }
    }

    internal sealed class GigDocument
    {
        public required string Id { get; set; }
        public required string OwnerId { get; set; }
        public required string ProfileId { get; set; }
        public required string BalanceId { get; set; }
        public string Kind { get; set; } = "gig";
        public required string Name { get; set; }
        public string? Description { get; set; }
        public DateOnly StartDate { get; set; }
        public long PayoutCents { get; set; }
        public string State { get; set; } = "planned";
        public long? CompletionAmountCents { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        [JsonProperty("_etag", NullValueHandling = NullValueHandling.Ignore)]
        public string? ETag { get; set; }
    }

    internal sealed class MutationReceipt
    {
        public required string Id { get; set; }
        public required string OwnerId { get; set; }
        public required string ProfileId { get; set; }
        public required string BalanceId { get; set; }
        public string Kind { get; set; } = "financial-mutation-receipt";
        public required string Fingerprint { get; set; }
        public required string ResourceType { get; set; }
        public required string ResourceId { get; set; }
        public string? Outcome { get; set; }
    }

    private const int ReceiptLimit = 10_000;

    private sealed record ReceiptRead(MutationReceipt Receipt, bool Matches);

    private static string Snapshot<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static T? Restore<T>(MutationReceipt receipt) =>
        string.IsNullOrEmpty(receipt.Outcome) ? default : System.Text.Json.JsonSerializer.Deserialize<T>(receipt.Outcome);

    public async Task<Balance?> GetOrCreateForProfileAsync(string ownerId, string profileId, CancellationToken cancellationToken)
    {
        var partition = new PartitionKey(ownerId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var profileResponse = await ReadProfile(ownerId, profileId, cancellationToken);
            if (profileResponse is null || profileResponse.Resource.Deleting)
                return null;
            var profile = profileResponse.Resource;
            if (profile.BalanceId is not null)
            {
                var existing = await GetAsync(ownerId, profile.BalanceId, cancellationToken);
                if (existing is not null)
                    return existing;
                var latest = await ReadProfile(ownerId, profileId, cancellationToken);
                if (latest is null || latest.Resource.Deleting)
                    return null;
                throw new InvalidOperationException("The profile balance is missing.");
            }

            var balance = new BalanceDocument
            {
                Id = Guid.NewGuid().ToString("N"), OwnerId = ownerId, ProfileId = profileId
            };
            profile.BalanceId = balance.Id;
            profile.ETag = null;
            using var batch = await container.CreateTransactionalBatch(partition)
                .ReplaceItem(profileId, profile, new TransactionalBatchItemRequestOptions { IfMatchEtag = profileResponse.ETag })
                .CreateItem(balance).ExecuteAsync(cancellationToken);
            if (batch.IsSuccessStatusCode)
                return ToBalance(balance, batch[1].ETag);
            if (batch.StatusCode == HttpStatusCode.PreconditionFailed)
                continue;
            throw new InvalidOperationException($"Balance initialization failed with Cosmos status {(int)batch.StatusCode}.");
        }
        throw new InvalidOperationException("Balance initialization could not be completed due to concurrent updates.");
    }

    public async Task<Balance?> GetAsync(string ownerId, string balanceId, CancellationToken cancellationToken)
    {
        var response = await ReadBalance(ownerId, balanceId, cancellationToken);
        if (response is null)
            return null;
        var profile = await ReadProfile(ownerId, response.Resource.ProfileId, cancellationToken);
        if (profile is null || profile.Resource.Deleting || profile.Resource.BalanceId != balanceId)
            return null;
        return ToBalance(response.Resource, response.ETag);
    }

    public async Task<BalanceUpdateResult> SetCurrencyAsync(string ownerId, string balanceId, string currency,
        string version, CancellationToken cancellationToken)
    {
        if (currency != "USD")
            throw new ArgumentOutOfRangeException(nameof(currency));
        var balance = await ReadBalance(ownerId, balanceId, cancellationToken);
        if (balance is null)
            return new BalanceUpdateResult(BalanceUpdateStatus.Missing);
        var profile = await ReadProfile(ownerId, balance.Resource.ProfileId, cancellationToken);
        if (profile is null || profile.Resource.Deleting || profile.Resource.BalanceId != balanceId)
            return new BalanceUpdateResult(BalanceUpdateStatus.Missing);
        if (balance.ETag != version || balance.Resource.Currency is not (null or "USD"))
            return new BalanceUpdateResult(BalanceUpdateStatus.Conflict);

        profile.Resource.ETag = null;
        balance.Resource.Currency = currency;
        balance.Resource.ETag = null;
        using var batch = await container.CreateTransactionalBatch(new PartitionKey(ownerId))
            .ReplaceItem(profile.Resource.Id, profile.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = profile.ETag })
            .ReplaceItem(balanceId, balance.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = version })
            .ExecuteAsync(cancellationToken);
        if (batch.IsSuccessStatusCode)
            return new BalanceUpdateResult(BalanceUpdateStatus.Updated, ToBalance(balance.Resource, batch[1].ETag));
        if (batch.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            return new BalanceUpdateResult(BalanceUpdateStatus.Conflict);
        throw new InvalidOperationException($"Balance update failed with Cosmos status {(int)batch.StatusCode}.");
    }

    public async Task<LedgerPage?> ListLedgerAsync(string ownerId, string balanceId, string? continuationToken, CancellationToken cancellationToken)
    {
        if (await GetAsync(ownerId, balanceId, cancellationToken) is null)
            return null;
        var query = new QueryDefinition("SELECT * FROM c WHERE c.kind = @kind AND c.balanceId = @balanceId ORDER BY c.createdAt DESC")
            .WithParameter("@kind", "balance-ledger").WithParameter("@balanceId", balanceId);
        try
        {
            using var iterator = container.GetItemQueryIterator<LedgerDocument>(query, continuationToken,
                new QueryRequestOptions { PartitionKey = new PartitionKey(ownerId), MaxItemCount = 25 });
            var page = await iterator.ReadNextAsync(cancellationToken);
            return new LedgerPage(page.Select(entry =>
            {
                if (entry.OwnerId != ownerId || entry.BalanceId != balanceId || entry.Kind != "balance-ledger")
                    throw new InvalidOperationException("The stored ledger entry is invalid.");
                return ToEntry(entry);
            }).ToArray(), page.ContinuationToken);
        }
        catch (CosmosException ex) when (continuationToken is not null && ex.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidBalanceContinuationException(ex);
        }
    }

    public async Task<LedgerEntry?> GetLedgerEntryAsync(string ownerId, string balanceId, string entryId, CancellationToken cancellationToken)
    {
        if (await GetAsync(ownerId, balanceId, cancellationToken) is null)
            return null;
        try
        {
            var entry = (await container.ReadItemAsync<LedgerDocument>(entryId, new PartitionKey(ownerId),
                cancellationToken: cancellationToken)).Resource;
            if (entry.Kind != "balance-ledger" || entry.OwnerId != ownerId || entry.BalanceId != balanceId)
                return null;
            return ToEntry(entry);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<LedgerWriteResult> AdjustCashAsync(string ownerId, string balanceId, Guid requestId, string operation,
        decimal amount, string version, CancellationToken cancellationToken)
    {
        if (operation is not ("deposit" or "withdrawal") || !FinancialAmounts.TryCents(amount, out var cents))
            throw new ArgumentOutOfRangeException(nameof(amount));
        var partition = new PartitionKey(ownerId);
        var receiptId = "financial-request-" + requestId.ToString("N");
        var fingerprint = AuthenticationService.Hash(System.Text.Json.JsonSerializer.Serialize(new { balanceId, operation, cents }));
        var replay = await ReadReceipt(ownerId, receiptId, fingerprint, cancellationToken);
        if (replay is not null)
            return replay;

        var balance = await ReadBalance(ownerId, balanceId, cancellationToken);
        if (balance is null)
            return await ReceiptOr(ownerId, receiptId, fingerprint, LedgerWriteStatus.Missing, cancellationToken);
        var profile = await ReadProfile(ownerId, balance.Resource.ProfileId, cancellationToken);
        if (profile is null || profile.Resource.Deleting || profile.Resource.BalanceId != balanceId)
            return await ReceiptOr(ownerId, receiptId, fingerprint, LedgerWriteStatus.Missing, cancellationToken);
        if (balance.ETag != version || balance.Resource.Currency != "USD")
            return await ReceiptOr(ownerId, receiptId, fingerprint, LedgerWriteStatus.Conflict, cancellationToken);
        if (balance.Resource.ReceiptCount >= ReceiptLimit)
            return new LedgerWriteResult(LedgerWriteStatus.ReceiptLimitReached);
        var delta = operation == "deposit" ? cents : -cents;
        var resultingCash = checked(balance.Resource.CashCents + delta);
        if (delta < 0 && resultingCash < 0)
            return await ReceiptOr(ownerId, receiptId, fingerprint, LedgerWriteStatus.InsufficientFunds, cancellationToken);
        if (!FinancialAmounts.IsRepresentable(resultingCash))
            return await ReceiptOr(ownerId, receiptId, fingerprint, LedgerWriteStatus.LimitExceeded, cancellationToken);

        var entry = new LedgerDocument
        {
            Id = Guid.NewGuid().ToString("N"), OwnerId = ownerId, ProfileId = profile.Resource.Id,
            BalanceId = balanceId, Operation = operation, AmountCents = delta,
            CashAfterCents = resultingCash, CreatedAt = clock.GetUtcNow()
        };
        var receipt = new FinancialReceipt
        {
            Id = receiptId, OwnerId = ownerId, ProfileId = profile.Resource.Id, BalanceId = balanceId,
            Fingerprint = fingerprint, Entry = entry
        };
        profile.Resource.ETag = null;
        balance.Resource.ETag = null;
        balance.Resource.CashCents = resultingCash;
        balance.Resource.ReceiptCount++;
        using var batch = await container.CreateTransactionalBatch(partition)
            .ReplaceItem(profile.Resource.Id, profile.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = profile.ETag })
            .ReplaceItem(balanceId, balance.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = version })
            .CreateItem(entry).CreateItem(receipt).ExecuteAsync(cancellationToken);
        if (batch.IsSuccessStatusCode)
            return new LedgerWriteResult(LedgerWriteStatus.Created, ToEntry(entry));
        if (batch.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            return await ReadReceipt(ownerId, receiptId, fingerprint, cancellationToken)
                ?? new LedgerWriteResult(LedgerWriteStatus.Conflict);
        throw new InvalidOperationException($"Cash adjustment failed with Cosmos status {(int)batch.StatusCode}.");
    }

    public async Task<InvestmentPage?> ListInvestmentsAsync(string ownerId, string balanceId, string? continuationToken, CancellationToken cancellationToken)
    {
        if (await GetAsync(ownerId, balanceId, cancellationToken) is null)
            return null;
        var query = new QueryDefinition("SELECT * FROM c WHERE c.kind = @kind AND c.balanceId = @balanceId")
            .WithParameter("@kind", "investment").WithParameter("@balanceId", balanceId);
        try
        {
            using var iterator = container.GetItemQueryIterator<InvestmentDocument>(query, continuationToken,
                new QueryRequestOptions { PartitionKey = new PartitionKey(ownerId), MaxItemCount = 25 });
            var page = await iterator.ReadNextAsync(cancellationToken);
            return new InvestmentPage(page.Select(doc =>
            {
                if (doc.OwnerId != ownerId || doc.BalanceId != balanceId)
                    throw new InvalidOperationException("The stored investment is invalid.");
                return ToInvestment(doc, doc.ETag);
            }).ToArray(), page.ContinuationToken);
        }
        catch (CosmosException ex) when (continuationToken is not null && ex.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidBalanceContinuationException(ex);
        }
    }

    public async Task<Investment?> GetInvestmentAsync(string ownerId, string balanceId, string investmentId, CancellationToken cancellationToken)
    {
        if (await GetAsync(ownerId, balanceId, cancellationToken) is null)
            return null;
        try
        {
            var response = await container.ReadItemAsync<InvestmentDocument>(investmentId, new PartitionKey(ownerId), cancellationToken: cancellationToken);
            var doc = response.Resource;
            if (doc.OwnerId != ownerId || doc.BalanceId != balanceId || doc.Kind != "investment")
                return null;
            return ToInvestment(doc, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<InvestmentWriteResult> CreateInvestmentAsync(string ownerId, string balanceId, Guid requestId,
        InvestmentKind kind, decimal annualRatePercent, CompoundingModel compounding,
        string? marketReference, string version, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(compounding))
            return new InvestmentWriteResult(InvestmentWriteStatus.ValidationFailed);
        if (kind == InvestmentKind.FixedRate
            && (!BalanceRules.IsPositiveAmount(annualRatePercent) && annualRatePercent != 0 ||
                annualRatePercent is < 0 or > 100 || decimal.Round(annualRatePercent, 2) != annualRatePercent ||
                !Enum.IsDefined(compounding)))
            return new InvestmentWriteResult(InvestmentWriteStatus.ValidationFailed);
        if (kind == InvestmentKind.MarketLinked && string.IsNullOrWhiteSpace(marketReference))
            return new InvestmentWriteResult(InvestmentWriteStatus.ValidationFailed);

        var partition = new PartitionKey(ownerId);
        var receiptId = "investment-request-" + requestId.ToString("N");
        var fingerprint = AuthenticationService.Hash(System.Text.Json.JsonSerializer.Serialize(new
        {
            balanceId, kind = kind.ToString(), annualRatePercent, compounding = compounding.ToString(), marketReference
        }));
        if (await ReadMutationReceipt(ownerId, receiptId, fingerprint, "investment", cancellationToken) is { } replay)
            return replay.Matches
                ? await ReplayInvestment(replay.Receipt, ownerId, balanceId, cancellationToken)
                : new InvestmentWriteResult(InvestmentWriteStatus.Conflict);

        var balance = await ReadBalance(ownerId, balanceId, cancellationToken);
        if (balance is null)
            return new InvestmentWriteResult(InvestmentWriteStatus.Missing);
        var profile = await ReadProfile(ownerId, balance.Resource.ProfileId, cancellationToken);
        if (profile is null || profile.Resource.Deleting || profile.Resource.BalanceId != balanceId)
            return new InvestmentWriteResult(InvestmentWriteStatus.Missing);
        if (balance.ETag != version || balance.Resource.Currency != "USD")
            return new InvestmentWriteResult(InvestmentWriteStatus.Conflict);
        if (balance.Resource.ReceiptCount >= ReceiptLimit)
            return new InvestmentWriteResult(InvestmentWriteStatus.ReceiptLimitReached);

        var doc = new InvestmentDocument
        {
            Id = Guid.NewGuid().ToString("N"), OwnerId = ownerId, ProfileId = profile.Resource.Id,
            BalanceId = balanceId, InvestmentKind = kind == InvestmentKind.FixedRate ? "fixed-rate" : "market-linked",
            State = "unfunded", AnnualRatePercent = annualRatePercent,
            Compounding = compounding.ToString().ToLowerInvariant(),
            MarketReference = kind == InvestmentKind.MarketLinked ? marketReference?.Trim() : null
        };
        var receipt = new MutationReceipt
        {
            Id = receiptId, OwnerId = ownerId, ProfileId = profile.Resource.Id, BalanceId = balanceId,
            Fingerprint = fingerprint, ResourceType = "investment", ResourceId = doc.Id,
            Outcome = Snapshot(ToInvestment(doc, "snapshot"))
        };
        profile.Resource.ETag = null;
        balance.Resource.ETag = null;
        balance.Resource.ReceiptCount++;
        using var batch = await container.CreateTransactionalBatch(partition)
            .ReplaceItem(profile.Resource.Id, profile.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = profile.ETag })
            .ReplaceItem(balanceId, balance.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = version })
            .CreateItem(doc).CreateItem(receipt).ExecuteAsync(cancellationToken);
        if (batch.IsSuccessStatusCode)
            return new InvestmentWriteResult(InvestmentWriteStatus.Created, ToInvestment(doc, batch[2].ETag));
        if (batch.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return await ReadMutationReceipt(ownerId, receiptId, fingerprint, "investment", cancellationToken) is { Matches: true } committed
                ? await ReplayInvestment(committed.Receipt, ownerId, balanceId, cancellationToken)
                : new InvestmentWriteResult(InvestmentWriteStatus.Conflict);
        }
        throw new InvalidOperationException($"Investment creation failed with Cosmos status {(int)batch.StatusCode}.");
    }

    public async Task<GigPage?> ListGigsAsync(string ownerId, string balanceId, string? continuationToken, CancellationToken cancellationToken)
    {
        if (await GetAsync(ownerId, balanceId, cancellationToken) is null)
            return null;
        var query = new QueryDefinition("SELECT * FROM c WHERE c.kind = @kind AND c.balanceId = @balanceId")
            .WithParameter("@kind", "gig").WithParameter("@balanceId", balanceId);
        try
        {
            using var iterator = container.GetItemQueryIterator<GigDocument>(query, continuationToken,
                new QueryRequestOptions { PartitionKey = new PartitionKey(ownerId), MaxItemCount = 25 });
            var page = await iterator.ReadNextAsync(cancellationToken);
            return new GigPage(page.Select(doc =>
            {
                if (doc.OwnerId != ownerId || doc.BalanceId != balanceId)
                    throw new InvalidOperationException("The stored gig is invalid.");
                return ToGig(doc, doc.ETag);
            }).ToArray(), page.ContinuationToken);
        }
        catch (CosmosException ex) when (continuationToken is not null && ex.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidBalanceContinuationException(ex);
        }
    }

    public async Task<Gig?> GetGigAsync(string ownerId, string balanceId, string gigId, CancellationToken cancellationToken)
    {
        if (await GetAsync(ownerId, balanceId, cancellationToken) is null)
            return null;
        try
        {
            var response = await container.ReadItemAsync<GigDocument>(gigId, new PartitionKey(ownerId), cancellationToken: cancellationToken);
            var doc = response.Resource;
            return doc.OwnerId == ownerId && doc.BalanceId == balanceId && doc.Kind == "gig"
                ? ToGig(doc, response.ETag) : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<GigWriteResult> CreateGigAsync(string ownerId, string balanceId, Guid requestId,
        string name, string? description, DateOnly startDate, decimal payout, GigState state,
        string version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Any(char.IsControl) ||
            description?.Length > 1000 || description?.Any(char.IsControl) == true ||
            !FinancialAmounts.TryCents(payout, out var payoutCents) || payoutCents <= 0 ||
            !Enum.IsDefined(state))
            return new GigWriteResult(GigWriteStatus.ValidationFailed);

        var partition = new PartitionKey(ownerId);
        var receiptId = "gig-request-" + requestId.ToString("N");
        var fingerprint = AuthenticationService.Hash(System.Text.Json.JsonSerializer.Serialize(new
        {
            balanceId, name = name.Trim(), description = description?.Trim(), startDate = startDate.ToString("yyyy-MM-dd"),
            payoutCents, state = state.ToString()
        }));
        if (await ReadMutationReceipt(ownerId, receiptId, fingerprint, "gig", cancellationToken) is { } replay)
            return replay.Matches
                ? await ReplayGig(replay.Receipt, ownerId, balanceId, cancellationToken)
                : new GigWriteResult(GigWriteStatus.Conflict);

        var balance = await ReadBalance(ownerId, balanceId, cancellationToken);
        if (balance is null)
            return new GigWriteResult(GigWriteStatus.Missing);
        var profile = await ReadProfile(ownerId, balance.Resource.ProfileId, cancellationToken);
        if (profile is null || profile.Resource.Deleting || profile.Resource.BalanceId != balanceId)
            return new GigWriteResult(GigWriteStatus.Missing);
        if (balance.ETag != version || balance.Resource.Currency != "USD")
            return new GigWriteResult(GigWriteStatus.Conflict);
        if (balance.Resource.ReceiptCount >= ReceiptLimit)
            return new GigWriteResult(GigWriteStatus.ReceiptLimitReached);

        var newGigId = Guid.NewGuid().ToString("N");
        var change = BalanceRules.ChangeGigState(null, state, payout);
        var resultingCash = balance.Resource.CashCents;
        LedgerDocument? entry = null;
        if (change.CreatesPayout)
        {
            resultingCash = checked(balance.Resource.CashCents + payoutCents);
            if (!FinancialAmounts.IsRepresentable(resultingCash))
                return new GigWriteResult(GigWriteStatus.ValidationFailed);
            entry = CreateLedgerEntry(ownerId, profile.Resource.Id, balanceId, "gig-payout",
                payoutCents, resultingCash, newGigId);
        }

        var doc = new GigDocument
        {
            Id = newGigId, OwnerId = ownerId, ProfileId = profile.Resource.Id,
            BalanceId = balanceId, Name = name.Trim(), Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            StartDate = startDate, PayoutCents = payoutCents, State = state.ToString().ToLowerInvariant(),
            CompletionAmountCents = change.CreatesPayout ? payoutCents : null,
            CompletedAt = change.CreatesPayout ? clock.GetUtcNow() : null
        };
        var receipt = new MutationReceipt
        {
            Id = receiptId, OwnerId = ownerId, ProfileId = profile.Resource.Id, BalanceId = balanceId,
            Fingerprint = fingerprint, ResourceType = "gig", ResourceId = doc.Id,
            Outcome = Snapshot(ToGig(doc, "snapshot"))
        };
        profile.Resource.ETag = null;
        balance.Resource.ETag = null;
        balance.Resource.CashCents = resultingCash;
        balance.Resource.ReceiptCount++;
        var batch = container.CreateTransactionalBatch(partition)
            .ReplaceItem(profile.Resource.Id, profile.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = profile.ETag })
            .ReplaceItem(balanceId, balance.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = version })
            .CreateItem(doc).CreateItem(receipt);
        if (entry is not null)
            batch.CreateItem(entry);
        using var result = await batch.ExecuteAsync(cancellationToken);
        if (result.IsSuccessStatusCode)
            return new GigWriteResult(GigWriteStatus.Created, ToGig(doc, result[2].ETag));
        if (result.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return await ReadMutationReceipt(ownerId, receiptId, fingerprint, "gig", cancellationToken) is { Matches: true } committed
                ? await ReplayGig(committed.Receipt, ownerId, balanceId, cancellationToken)
                : new GigWriteResult(GigWriteStatus.Conflict);
        }
        throw new InvalidOperationException($"Gig creation failed with Cosmos status {(int)result.StatusCode}.");
    }

    public async Task<GigWriteResult> UpdateGigAsync(string ownerId, string balanceId, string gigId, Guid requestId,
        string name, string? description, DateOnly startDate, decimal payout, GigState state,
        string version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Any(char.IsControl) ||
            description?.Length > 1000 || description?.Any(char.IsControl) == true ||
            !FinancialAmounts.TryCents(payout, out var payoutCents) || payoutCents <= 0 ||
            !Enum.IsDefined(state))
            return new GigWriteResult(GigWriteStatus.ValidationFailed);

        var partition = new PartitionKey(ownerId);
        var receiptId = "gig-request-" + requestId.ToString("N");
        var fingerprint = AuthenticationService.Hash(System.Text.Json.JsonSerializer.Serialize(new
        {
            balanceId, gigId, name = name.Trim(), description = description?.Trim(),
            startDate = startDate.ToString("yyyy-MM-dd"), payoutCents, state = state.ToString()
        }));
        if (await ReadMutationReceipt(ownerId, receiptId, fingerprint, "gig", cancellationToken) is { } replay)
            return replay.Matches
                ? await ReplayGig(replay.Receipt, ownerId, balanceId, cancellationToken)
                : new GigWriteResult(GigWriteStatus.Conflict);

        ItemResponse<GigDocument> gigResponse;
        try
        {
            gigResponse = await container.ReadItemAsync<GigDocument>(gigId, partition, cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new GigWriteResult(GigWriteStatus.Missing);
        }
        var gig = gigResponse.Resource;
        var balance = await ReadBalance(ownerId, balanceId, cancellationToken);
        var profile = balance is null ? null : await ReadProfile(ownerId, balance.Resource.ProfileId, cancellationToken);
        if (gig.Kind != "gig" || gig.OwnerId != ownerId || gig.BalanceId != balanceId ||
            balance is null || profile is null || profile.Resource.Deleting ||
            profile.Resource.BalanceId != balanceId || gig.ProfileId != profile.Resource.Id)
            return new GigWriteResult(GigWriteStatus.Missing);
        if (gigResponse.ETag != version)
            return new GigWriteResult(GigWriteStatus.Conflict);
        if (balance.Resource.ReceiptCount >= ReceiptLimit)
            return new GigWriteResult(GigWriteStatus.ReceiptLimitReached);
        if (!Enum.TryParse<GigState>(gig.State, true, out var previous))
            throw new InvalidOperationException("The stored gig state is invalid.");
        if (previous == GigState.Completed && state == GigState.Completed)
            return new GigWriteResult(GigWriteStatus.Conflict);
        if (previous == GigState.Completed)
        {
            name = gig.Name;
            description = gig.Description;
            startDate = gig.StartDate;
            payoutCents = gig.PayoutCents;
        }

        var change = BalanceRules.ChangeGigState(previous, state, payoutCents / 100m);
        var deltaCents = change.CreatesPayout ? payoutCents : change.ReversesPayout ? -payoutCents : 0;
        var resultingCash = checked(balance.Resource.CashCents + deltaCents);
        if (!change.ReversesPayout && resultingCash < 0)
            return new GigWriteResult(GigWriteStatus.Conflict);
        if (!FinancialAmounts.IsRepresentable(resultingCash))
            return new GigWriteResult(GigWriteStatus.ValidationFailed);

        LedgerDocument? entry = null;
        if (change.CreatesPayout || change.ReversesPayout)
            entry = CreateLedgerEntry(ownerId, profile.Resource.Id, balanceId,
                change.CreatesPayout ? "gig-payout" : "gig-payout-reversal",
                deltaCents, resultingCash, gigId);

        gig.Name = name.Trim();
        gig.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        gig.StartDate = startDate;
        gig.PayoutCents = payoutCents;
        gig.State = state.ToString().ToLowerInvariant();
        if (change.CreatesPayout)
        {
            gig.CompletionAmountCents = payoutCents;
            gig.CompletedAt = clock.GetUtcNow();
        }
        else if (change.ReversesPayout)
        {
            gig.CompletionAmountCents = null;
            gig.CompletedAt = null;
        }
        gig.ETag = null;
        balance.Resource.CashCents = resultingCash;
        balance.Resource.ReceiptCount++;
        balance.Resource.ETag = null;
        profile.Resource.ETag = null;
        var receipt = new MutationReceipt
        {
            Id = receiptId, OwnerId = ownerId, ProfileId = profile.Resource.Id, BalanceId = balanceId,
            Fingerprint = fingerprint, ResourceType = "gig", ResourceId = gigId,
            Outcome = Snapshot(ToGig(gig, "snapshot"))
        };
        var batch = container.CreateTransactionalBatch(partition)
            .ReplaceItem(profile.Resource.Id, profile.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = profile.ETag })
            .ReplaceItem(balanceId, balance.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = balance.ETag })
            .ReplaceItem(gigId, gig, new TransactionalBatchItemRequestOptions { IfMatchEtag = version })
            .CreateItem(receipt);
        if (entry is not null)
            batch.CreateItem(entry);
        using var result = await batch.ExecuteAsync(cancellationToken);
        if (result.IsSuccessStatusCode)
            return new GigWriteResult(GigWriteStatus.Updated, ToGig(gig, result[2].ETag));
        if (result.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            return await ReadMutationReceipt(ownerId, receiptId, fingerprint, "gig", cancellationToken) is { Matches: true } committed
                ? await ReplayGig(committed.Receipt, ownerId, balanceId, cancellationToken)
                : new GigWriteResult(GigWriteStatus.Conflict);
        throw new InvalidOperationException($"Gig update failed with Cosmos status {(int)result.StatusCode}.");
    }

    public async Task<TransitionResult> TransitionAsync(string ownerId, string balanceId, Guid requestId,
        string source, string destination, decimal amount, string sourceVersion, string destinationVersion,
        string balanceVersion, CancellationToken cancellationToken)
    {
        if (!FinancialAmounts.TryCents(amount, out var amountCents) || amountCents <= 0)
            return new TransitionResult(TransitionStatus.ValidationFailed, 0m, balanceVersion);
        var receiptId = "transition-request-" + requestId.ToString("N");
        var fingerprint = AuthenticationService.Hash(System.Text.Json.JsonSerializer.Serialize(new
        { balanceId, source, destination, amountCents }));
        if (await ReadMutationReceipt(ownerId, receiptId, fingerprint, "transition", cancellationToken) is { } prior)
            return prior.Matches
                ? await ReplayTransition(prior.Receipt, ownerId, balanceId, cancellationToken)
                : new TransitionResult(TransitionStatus.Conflict, 0m, balanceVersion);

        var balance = await ReadBalance(ownerId, balanceId, cancellationToken);
        if (balance is null)
            return new TransitionResult(TransitionStatus.Missing, 0m, balanceVersion);
        var profile = await ReadProfile(ownerId, balance.Resource.ProfileId, cancellationToken);
        if (profile is null || profile.Resource.Deleting || profile.Resource.BalanceId != balanceId)
            return new TransitionResult(TransitionStatus.Missing, 0m, balanceVersion);
        TransitionResult Failure(TransitionStatus status) =>
            new(status, balance.Resource.CashCents / 100m, balance.ETag ?? balanceVersion);
        if (balance.ETag != balanceVersion || balance.Resource.Currency != "USD")
            return Failure(TransitionStatus.Conflict);
        // Transitions out of investments need valuation, which is not available yet.
        if (source != "cash" || destination == "cash")
            return Failure(source == "cash" ? TransitionStatus.ValidationFailed : TransitionStatus.ValueUnavailable);
        if (balance.Resource.ReceiptCount >= ReceiptLimit)
            return Failure(TransitionStatus.ReceiptLimitReached);

        ItemResponse<InvestmentDocument> destinationResponse;
        try
        {
            destinationResponse = await container.ReadItemAsync<InvestmentDocument>(destination, new PartitionKey(ownerId), cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return Failure(TransitionStatus.ValidationFailed);
        }
        var doc = destinationResponse.Resource;
        if (doc.Kind != "investment" || doc.OwnerId != ownerId || doc.BalanceId != balanceId ||
            doc.ProfileId != profile.Resource.Id)
            return Failure(TransitionStatus.ValidationFailed);
        if (destinationResponse.ETag != destinationVersion)
            return Failure(TransitionStatus.Conflict);
        if (doc.State != "unfunded" || doc.InvestmentKind != "fixed-rate")
            return Failure(TransitionStatus.ValidationFailed);
        if (balance.Resource.CashCents < amountCents)
            return Failure(TransitionStatus.InsufficientFunds);

        var resultingCash = balance.Resource.CashCents - amountCents;
        doc.State = "active";
        doc.PrincipalCents = amountCents;
        doc.FundedOn = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        doc.ETag = null;
        balance.Resource.CashCents = resultingCash;
        balance.Resource.ReceiptCount++;
        balance.Resource.ETag = null;
        profile.Resource.ETag = null;
        var entry = CreateLedgerEntry(ownerId, profile.Resource.Id, balanceId, "investment-funding",
            -amountCents, resultingCash, doc.Id);
        var receipt = new MutationReceipt
        {
            Id = receiptId, OwnerId = ownerId, ProfileId = profile.Resource.Id, BalanceId = balanceId,
            Fingerprint = fingerprint, ResourceType = "transition", ResourceId = doc.Id,
            Outcome = Snapshot(new TransitionResult(TransitionStatus.Updated, resultingCash / 100m, "snapshot",
                Destination: ToInvestment(doc, "snapshot"), Entry: ToEntry(entry)))
        };
        using var batch = await container.CreateTransactionalBatch(new PartitionKey(ownerId))
            .ReplaceItem(profile.Resource.Id, profile.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = profile.ETag })
            .ReplaceItem(balanceId, balance.Resource, new TransactionalBatchItemRequestOptions { IfMatchEtag = balanceVersion })
            .ReplaceItem(destination, doc, new TransactionalBatchItemRequestOptions { IfMatchEtag = destinationVersion })
            .CreateItem(entry).CreateItem(receipt).ExecuteAsync(cancellationToken);
        if (batch.IsSuccessStatusCode)
            return new TransitionResult(TransitionStatus.Updated, resultingCash / 100m, batch[1].ETag,
                Destination: ToInvestment(doc, batch[2].ETag), Entry: ToEntry(entry));
        if (batch.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            return await ReadMutationReceipt(ownerId, receiptId, fingerprint, "transition", cancellationToken) is { Matches: true } committed
                ? await ReplayTransition(committed.Receipt, ownerId, balanceId, cancellationToken)
                : new TransitionResult(TransitionStatus.Conflict, 0m, balanceVersion);
        throw new InvalidOperationException($"Transition failed with Cosmos status {(int)batch.StatusCode}.");
    }

    private async Task<TransitionResult> ReplayTransition(MutationReceipt receipt, string ownerId,
        string balanceId, CancellationToken cancellationToken)
    {
        var snapshot = Restore<TransitionResult>(receipt);
        var balance = await ReadBalance(ownerId, balanceId, cancellationToken);
        var current = await GetInvestmentAsync(ownerId, balanceId, receipt.ResourceId, cancellationToken);
        if (snapshot?.Destination is null || balance is null || current is null)
            return new TransitionResult(TransitionStatus.Missing, 0m, "");
        return snapshot with
        {
            Status = TransitionStatus.Replayed,
            BalanceVersion = balance.ETag ?? "",
            Destination = snapshot.Destination with { Version = current.Version }
        };
    }

    private LedgerDocument CreateLedgerEntry(string ownerId, string profileId, string balanceId,
        string operation, long amountCents, long cashAfterCents, string? sourceId = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"), OwnerId = ownerId, ProfileId = profileId,
            BalanceId = balanceId, Operation = operation, AmountCents = amountCents,
            CashAfterCents = cashAfterCents, SourceId = sourceId, CreatedAt = clock.GetUtcNow()
        };

    private async Task<InvestmentWriteResult> ReplayInvestment(MutationReceipt receipt, string ownerId,
        string balanceId, CancellationToken cancellationToken)
    {
        var snapshot = Restore<Investment>(receipt);
        var current = await GetInvestmentAsync(ownerId, balanceId, receipt.ResourceId, cancellationToken);
        return snapshot is null || current is null
            ? new InvestmentWriteResult(InvestmentWriteStatus.Missing)
            : new InvestmentWriteResult(InvestmentWriteStatus.Created, snapshot with { Version = current.Version });
    }

    private async Task<GigWriteResult> ReplayGig(MutationReceipt receipt, string ownerId,
        string balanceId, CancellationToken cancellationToken)
    {
        var snapshot = Restore<Gig>(receipt);
        var current = await GetGigAsync(ownerId, balanceId, receipt.ResourceId, cancellationToken);
        return snapshot is null || current is null
            ? new GigWriteResult(GigWriteStatus.Missing)
            : new GigWriteResult(GigWriteStatus.Replayed, snapshot with { Version = current.Version });
    }

    private async Task<ReceiptRead?> ReadMutationReceipt(string ownerId, string receiptId,
        string fingerprint, string resourceType, CancellationToken cancellationToken)
    {
        try
        {
            var receipt = (await container.ReadItemAsync<MutationReceipt>(receiptId, new PartitionKey(ownerId),
                cancellationToken: cancellationToken)).Resource;
            if (receipt.OwnerId != ownerId || receipt.Id != receiptId || receipt.Kind != "financial-mutation-receipt" ||
                receipt.ResourceType != resourceType || string.IsNullOrEmpty(receipt.Fingerprint))
                throw new InvalidOperationException("The stored financial mutation receipt is invalid.");
            return new ReceiptRead(receipt, AuthenticationService.FixedEquals(receipt.Fingerprint, fingerprint));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static Investment ToInvestment(InvestmentDocument doc, string? version)
    {
        if (string.IsNullOrEmpty(version) ||
            !Enum.TryParse<InvestmentKind>(doc.InvestmentKind == "market-linked" ? "MarketLinked" : "FixedRate", out var kind) ||
            !Enum.TryParse<InvestmentState>(doc.State, true, out var state) ||
            !Enum.TryParse<CompoundingModel>(doc.Compounding, true, out var compounding))
            throw new InvalidOperationException("The stored investment is invalid.");
        return new Investment(doc.Id, doc.BalanceId, kind, state, doc.AnnualRatePercent,
            compounding, doc.PrincipalCents / 100m, doc.FundedOn, doc.ClosedOn,
            doc.ClosingValueCents.HasValue ? doc.ClosingValueCents.Value / 100m : null, version);
    }

    private static Gig ToGig(GigDocument doc, string? version)
    {
        if (string.IsNullOrEmpty(version) || !Enum.TryParse<GigState>(doc.State, true, out var state))
            throw new InvalidOperationException("The stored gig is invalid.");
        return new Gig(doc.Id, doc.BalanceId, doc.Name, doc.Description, doc.StartDate,
            doc.PayoutCents / 100m, state,
            doc.CompletionAmountCents.HasValue ? doc.CompletionAmountCents.Value / 100m : null,
            doc.CompletedAt, version);
    }

    private async Task<LedgerWriteResult> ReceiptOr(string ownerId, string receiptId, string fingerprint,
        LedgerWriteStatus otherwise, CancellationToken cancellationToken) =>
        await ReadReceipt(ownerId, receiptId, fingerprint, cancellationToken) ?? new LedgerWriteResult(otherwise);

    private async Task<LedgerWriteResult?> ReadReceipt(string ownerId, string receiptId, string fingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = (await container.ReadItemAsync<FinancialReceipt>(receiptId, new PartitionKey(ownerId),
                cancellationToken: cancellationToken)).Resource;
            if (receipt.OwnerId != ownerId || receipt.Id != receiptId || receipt.Kind != "financial-receipt" ||
                receipt.Entry is null || receipt.Entry.Kind != "balance-ledger" ||
                receipt.Entry.OwnerId != ownerId || receipt.Entry.ProfileId != receipt.ProfileId ||
                receipt.Entry.BalanceId != receipt.BalanceId || string.IsNullOrEmpty(receipt.Fingerprint))
                throw new InvalidOperationException("The stored financial receipt is invalid.");
            return AuthenticationService.FixedEquals(receipt.Fingerprint, fingerprint)
                ? new LedgerWriteResult(LedgerWriteStatus.Replayed, ToEntry(receipt.Entry))
                : new LedgerWriteResult(LedgerWriteStatus.Conflict);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static LedgerEntry ToEntry(LedgerDocument entry) => new(
        entry.Id, entry.BalanceId, entry.Operation, entry.AmountCents / 100m, entry.CashAfterCents / 100m, entry.CreatedAt);

    private async Task<ItemResponse<ProfileDocument>?> ReadProfile(string ownerId, string profileId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await container.ReadItemAsync<ProfileDocument>(profileId, new PartitionKey(ownerId), cancellationToken: cancellationToken);
            if (result.Resource.Kind != "managed-profile" || result.Resource.OwnerId != ownerId || result.Resource.Id != profileId)
                throw new InvalidOperationException("The stored managed profile is invalid.");
            return result;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<ItemResponse<BalanceDocument>?> ReadBalance(string ownerId, string balanceId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await container.ReadItemAsync<BalanceDocument>(balanceId, new PartitionKey(ownerId), cancellationToken: cancellationToken);
            if (result.Resource.Kind != "balance" || result.Resource.OwnerId != ownerId || result.Resource.Id != balanceId ||
                !Guid.TryParseExact(result.Resource.ProfileId, "N", out _) ||
                !FinancialAmounts.IsRepresentable(result.Resource.CashCents))
                throw new InvalidOperationException("The stored balance is invalid.");
            return result;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static Balance ToBalance(BalanceDocument document, string? version) =>
        !string.IsNullOrEmpty(version)
            ? new Balance(document.Id, document.ProfileId, document.Currency, document.CashCents / 100m, version)
            : throw new InvalidOperationException("The stored balance has no version.");
}
