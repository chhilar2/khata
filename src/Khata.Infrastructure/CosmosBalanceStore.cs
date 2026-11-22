using System.Net;
using Khata.Application;
using Microsoft.Azure.Cosmos;
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
