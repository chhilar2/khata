using System.Net;
using Khata.Application;
using Microsoft.Azure.Cosmos;

namespace Khata.Infrastructure;

public sealed class CosmosAccountStore(Container container, TimeProvider clock) : IAccountStore
{
    internal sealed class Document
    {
        public required string Id { get; set; }
        public required string PartitionKey { get; set; }
        public required string Kind { get; set; }
        public required string UserId { get; set; }
        public required string Provider { get; set; }
        public string? Email { get; set; }
        public string? DisplayName { get; set; }
        public string? ContactEmail { get; set; }
        public string? Phone { get; set; }
        public string? Country { get; set; }
        public string? Currency { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public async Task EnsureProfileAsync(string userId, ValidatedIdentity identity, CancellationToken cancellationToken)
    {
        var key = Key(userId);
        var now = clock.GetUtcNow();
        var document = new Document
        {
            Id = key, PartitionKey = key, Kind = "profile", UserId = userId,
            Provider = identity.Identity.Provider, Email = identity.Email, DisplayName = identity.DisplayName,
            CreatedAt = now, UpdatedAt = now
        };
        try
        {
            await container.CreateItemAsync(document, new PartitionKey(key), cancellationToken: cancellationToken);
            return;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await container.ReadItemAsync<Document>(key, new PartitionKey(key), cancellationToken: cancellationToken);
            Validate(response.Resource, userId);
            if (response.Resource.Provider == identity.Identity.Provider && response.Resource.Email == identity.Email)
                return;
            response.Resource.Provider = identity.Identity.Provider;
            response.Resource.Email = identity.Email;
            response.Resource.UpdatedAt = clock.GetUtcNow();
            try
            {
                await container.ReplaceItemAsync(response.Resource, key, new PartitionKey(key),
                    new ItemRequestOptions { IfMatchEtag = response.ETag }, cancellationToken);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < 4)
            {
            }
        }
    }

    public async Task<VersionedAccount?> GetAsync(string userId, CancellationToken cancellationToken)
    {
        var key = Key(userId);
        try
        {
            var response = await container.ReadItemAsync<Document>(key, new PartitionKey(key), cancellationToken: cancellationToken);
            Validate(response.Resource, userId);
            var doc = response.Resource;
            return new VersionedAccount(new AccountProfile(doc.UserId, doc.Provider, doc.Email,
                new AccountFields(doc.DisplayName, doc.ContactEmail, doc.Phone, doc.Country, doc.Currency),
                doc.CreatedAt, doc.UpdatedAt), response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<AccountUpdateResult> UpdateAsync(string userId, AccountFields fields, string version, CancellationToken cancellationToken)
    {
        var key = Key(userId);
        try
        {
            var response = await container.ReadItemAsync<Document>(key, new PartitionKey(key), cancellationToken: cancellationToken);
            var doc = response.Resource;
            Validate(doc, userId);
            doc.DisplayName = fields.DisplayName;
            doc.ContactEmail = fields.ContactEmail;
            doc.Phone = fields.Phone;
            doc.Country = fields.Country;
            doc.Currency = fields.Currency;
            doc.UpdatedAt = clock.GetUtcNow();
            await container.ReplaceItemAsync(doc, key, new PartitionKey(key),
                new ItemRequestOptions { IfMatchEtag = version }, cancellationToken);
            return AccountUpdateResult.Updated;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return AccountUpdateResult.Conflict;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return AccountUpdateResult.Missing;
        }
    }

    private static string Key(string userId) => "profile-" + userId;

    private static void Validate(Document doc, string userId)
    {
        var key = Key(userId);
        if (doc.Kind != "profile" || doc.Id != key || doc.PartitionKey != key ||
            doc.UserId != userId || string.IsNullOrWhiteSpace(doc.Provider))
            throw new InvalidOperationException("The stored account profile is invalid.");
    }
}
