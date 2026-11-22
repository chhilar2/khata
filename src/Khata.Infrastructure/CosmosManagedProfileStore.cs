using System.Net;
using Khata.Application;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

namespace Khata.Infrastructure;

public sealed class CosmosManagedProfileStore(Container container, TimeProvider clock) : IManagedProfileStore
{
    internal sealed class Document
    {
        public required string Id { get; set; }
        public required string OwnerId { get; set; }
        public required string Kind { get; set; }
        public string? DisplayName { get; set; }
        public string? DateOfBirth { get; set; }
        public string? BalanceId { get; set; }
        public bool Deleting { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        [JsonProperty("_etag", NullValueHandling = NullValueHandling.Ignore)]
        public string? ETag { get; set; }
    }

    internal sealed class BalanceDocument
    {
        public required string Id { get; set; }
        public required string OwnerId { get; set; }
        public required string ProfileId { get; set; }
        public string Kind { get; set; } = "balance";
        public string? Currency { get; set; }
        public long CashCents { get; set; }
        [JsonProperty("_etag", NullValueHandling = NullValueHandling.Ignore)]
        public string? ETag { get; set; }
    }

    internal sealed class ProfileChild
    {
        public required string Id { get; set; }
    }

    internal sealed class Receipt
    {
        public required string Id { get; set; }
        public required string OwnerId { get; set; }
        public required string Kind { get; set; }
        public required string ProfileId { get; set; }
        public required string Fingerprint { get; set; }
        public int Ttl { get; set; }
    }

    public async Task<ProfileCreateResult> CreateAsync(string ownerId, Guid requestId, ManagedProfileFields fields, CancellationToken cancellationToken)
    {
        var partition = new PartitionKey(ownerId);
        var now = clock.GetUtcNow();
        var doc = new Document
        {
            Id = Guid.NewGuid().ToString("N"), OwnerId = ownerId, Kind = "managed-profile",
            DisplayName = fields.DisplayName, DateOfBirth = fields.DateOfBirth,
            CreatedAt = now, UpdatedAt = now, BalanceId = Guid.NewGuid().ToString("N")
        };
        var balance = new BalanceDocument { Id = doc.BalanceId, OwnerId = ownerId, ProfileId = doc.Id };
        var fingerprint = AuthenticationService.Hash(System.Text.Json.JsonSerializer.Serialize(fields));
        var receipt = new Receipt
        {
            Id = "request-" + requestId.ToString("N"), OwnerId = ownerId, Kind = "create-receipt",
            ProfileId = doc.Id, Fingerprint = fingerprint, Ttl = 86400
        };
        using var batch = await container.CreateTransactionalBatch(partition)
            .CreateItem(receipt).CreateItem(doc).CreateItem(balance).ExecuteAsync(cancellationToken);
        if (batch.IsSuccessStatusCode)
            return new ProfileCreateResult(ProfileCreateStatus.Created, ToProfile(doc, batch[1].ETag));
        if (batch.StatusCode != HttpStatusCode.Conflict)
            throw new InvalidOperationException($"Profile creation failed with Cosmos status {(int)batch.StatusCode}.");

        Receipt stored;
        try
        {
            stored = (await container.ReadItemAsync<Receipt>(receipt.Id, partition, cancellationToken: cancellationToken)).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("The profile creation conflict could not be resolved.", ex);
        }
        if (stored.Kind != "create-receipt" || stored.OwnerId != ownerId || stored.Id != receipt.Id ||
            string.IsNullOrEmpty(stored.ProfileId) || string.IsNullOrEmpty(stored.Fingerprint))
            throw new InvalidOperationException("The stored profile creation receipt is invalid.");
        if (!AuthenticationService.FixedEquals(stored.Fingerprint, fingerprint))
            return new ProfileCreateResult(ProfileCreateStatus.Conflict, null);
        var existing = await GetAsync(ownerId, stored.ProfileId, cancellationToken);
        return existing is null
            ? new ProfileCreateResult(ProfileCreateStatus.Gone, null)
            : new ProfileCreateResult(ProfileCreateStatus.Replayed, existing);
    }

    public async Task<ManagedProfilePage> ListAsync(string ownerId, string? continuationToken, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.kind = @kind")
            .WithParameter("@kind", "managed-profile");
        try
        {
            using var iterator = container.GetItemQueryIterator<Document>(query, continuationToken,
                new QueryRequestOptions { PartitionKey = new PartitionKey(ownerId), MaxItemCount = 25 });
            var result = await iterator.ReadNextAsync(cancellationToken);
            return new ManagedProfilePage(result.Select(doc =>
            {
                Validate(doc, ownerId);
                return ToProfile(doc, doc.ETag);
            }).ToArray(), result.ContinuationToken);
        }
        catch (CosmosException ex) when (continuationToken is not null && ex.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidProfileContinuationException(ex);
        }
    }

    public async Task<ManagedProfile?> GetAsync(string ownerId, string id, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<Document>(id, new PartitionKey(ownerId), cancellationToken: cancellationToken);
            Validate(response.Resource, ownerId);
            return ToProfile(response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ProfileWriteResult> UpdateAsync(string ownerId, string id, ManagedProfileFields fields, string version, CancellationToken cancellationToken)
    {
        try
        {
            var partition = new PartitionKey(ownerId);
            var current = await container.ReadItemAsync<Document>(id, partition, cancellationToken: cancellationToken);
            Validate(current.Resource, ownerId);
            var doc = current.Resource;
            if (doc.Deleting)
                return new ProfileWriteResult(ProfileWriteStatus.Conflict);
            doc.DisplayName = fields.DisplayName;
            doc.DateOfBirth = fields.DateOfBirth;
            doc.UpdatedAt = clock.GetUtcNow();
            doc.ETag = null;
            var result = await container.ReplaceItemAsync(doc, id, partition,
                new ItemRequestOptions { IfMatchEtag = version }, cancellationToken);
            return new ProfileWriteResult(ProfileWriteStatus.Updated, ToProfile(result.Resource, result.ETag));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProfileWriteResult(ProfileWriteStatus.Missing);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return new ProfileWriteResult(ProfileWriteStatus.Conflict);
        }
    }

    public async Task<ProfileWriteResult> DeleteAsync(string ownerId, string id, string version, CancellationToken cancellationToken)
    {
        try
        {
            var partition = new PartitionKey(ownerId);
            var current = await container.ReadItemAsync<Document>(id, partition, cancellationToken: cancellationToken);
            Validate(current.Resource, ownerId);
            if (current.ETag != version)
                return new ProfileWriteResult(ProfileWriteStatus.Conflict);

            var doc = current.Resource;
            if (!doc.Deleting)
            {
                doc.Deleting = true;
                doc.UpdatedAt = clock.GetUtcNow();
                doc.ETag = null;
                var marked = await container.ReplaceItemAsync(doc, id, partition,
                    new ItemRequestOptions { IfMatchEtag = version }, cancellationToken);
                version = marked.ETag;
            }

            var query = new QueryDefinition("SELECT c.id FROM c WHERE c.profileId = @profileId AND c.kind != @receiptKind")
                .WithParameter("@receiptKind", "create-receipt")
                .WithParameter("@profileId", id);
            using var iterator = container.GetItemQueryIterator<ProfileChild>(query, requestOptions:
                new QueryRequestOptions { PartitionKey = partition, MaxItemCount = 50 });
            while (iterator.HasMoreResults)
            {
                var children = await iterator.ReadNextAsync(cancellationToken);
                if (!children.Any())
                    continue;
                doc.ETag = null;
                var batch = container.CreateTransactionalBatch(partition)
                    .ReplaceItem(id, doc, new TransactionalBatchItemRequestOptions { IfMatchEtag = version });
                foreach (var child in children)
                    batch.DeleteItem(child.Id);
                using var result = await batch.ExecuteAsync(cancellationToken);
                if (result.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
                    return new ProfileWriteResult(ProfileWriteStatus.Conflict);
                if (!result.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Profile cleanup failed with Cosmos status {(int)result.StatusCode}.");
                return new ProfileWriteResult(ProfileWriteStatus.DeletionPending, ToProfile(doc, result[0].ETag));
            }

            var receiptQuery = new QueryDefinition("SELECT c.id FROM c WHERE c.profileId = @profileId AND c.kind = @receiptKind")
                .WithParameter("@profileId", id).WithParameter("@receiptKind", "create-receipt");
            using var receipts = container.GetItemQueryIterator<ProfileChild>(receiptQuery, requestOptions:
                new QueryRequestOptions { PartitionKey = partition, MaxItemCount = 2 });
            var receiptIds = new List<string>();
            while (receipts.HasMoreResults)
            {
                var page = await receipts.ReadNextAsync(cancellationToken);
                receiptIds.AddRange(page.Select(receipt => receipt.Id));
                if (receiptIds.Count > 1)
                    throw new InvalidOperationException("The profile has multiple creation receipts.");
            }
            if (receiptIds.Count == 0)
            {
                await container.DeleteItemAsync<Document>(id, partition,
                    new ItemRequestOptions { IfMatchEtag = version }, cancellationToken);
                return new ProfileWriteResult(ProfileWriteStatus.Deleted);
            }
            using var finalBatch = await container.CreateTransactionalBatch(partition)
                .DeleteItem(receiptIds[0])
                .DeleteItem(id, new TransactionalBatchItemRequestOptions { IfMatchEtag = version })
                .ExecuteAsync(cancellationToken);
            if (finalBatch.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
                return new ProfileWriteResult(ProfileWriteStatus.Conflict);
            if (finalBatch.StatusCode == HttpStatusCode.NotFound)
                return new ProfileWriteResult(ProfileWriteStatus.DeletionPending, ToProfile(doc, version));
            if (!finalBatch.IsSuccessStatusCode)
                throw new InvalidOperationException($"Profile deletion failed with Cosmos status {(int)finalBatch.StatusCode}.");
            return new ProfileWriteResult(ProfileWriteStatus.Deleted);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProfileWriteResult(ProfileWriteStatus.Missing);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return new ProfileWriteResult(ProfileWriteStatus.Conflict);
        }
    }

    private static void Validate(Document doc, string ownerId)
    {
        if (doc.Kind != "managed-profile" || doc.OwnerId != ownerId || !Guid.TryParseExact(doc.Id, "N", out _) ||
            string.IsNullOrWhiteSpace(doc.DisplayName))
            throw new InvalidOperationException("The stored managed profile is invalid.");
    }

    private static ManagedProfile ToProfile(Document doc, string? version) =>
        !string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(doc.DisplayName)
            ? new ManagedProfile(doc.Id, doc.DisplayName, doc.DateOfBirth, doc.CreatedAt, doc.UpdatedAt, version, doc.Deleting)
            : throw new InvalidOperationException("The stored managed profile is missing required data.");
}
