using System.Net;
using Khata.Application;
using Microsoft.Azure.Cosmos;

namespace Khata.Infrastructure;

public sealed class CosmosAuthenticationStore(Container container, TimeProvider clock) : IAuthenticationStore
{
    private sealed class Document
    {
        public required string Id { get; set; }
        public string? Kind { get; set; }
        public string? BindingHash { get; set; }
        public string? NonceHash { get; set; }
        public string? Provider { get; set; }
        public string? Issuer { get; set; }
        public string? Subject { get; set; }
        public string? UserId { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public int? Ttl { get; set; }
    }

    public async Task SaveFlowAsync(LoginFlow flow, CancellationToken cancellationToken) =>
        await container.CreateItemAsync(new Document
        {
            Id = "flow-" + flow.Id, Kind = "flow", BindingHash = flow.BindingHash, NonceHash = flow.NonceHash,
            ExpiresAt = flow.ExpiresAt, Ttl = 600
        }, new PartitionKey("flow-" + flow.Id), cancellationToken: cancellationToken);

    public async Task<LoginFlow?> ConsumeFlowAsync(string id, string bindingHash, CancellationToken cancellationToken)
    {
        var key = "flow-" + id;
        try
        {
            var response = await container.ReadItemAsync<Document>(key, new PartitionKey(key), cancellationToken: cancellationToken);
            var doc = response.Resource;
            if (doc.Kind != "flow" || doc.BindingHash is null || doc.NonceHash is null ||
                doc.ExpiresAt <= clock.GetUtcNow() || !AuthenticationService.FixedEquals(doc.BindingHash, bindingHash))
                return null;
            await container.DeleteItemAsync<Document>(key, new PartitionKey(key),
                new ItemRequestOptions { IfMatchEtag = response.ETag }, cancellationToken);
            return new LoginFlow(id, doc.BindingHash, doc.NonceHash, doc.ExpiresAt);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    public async Task<UserIdentity> GetOrCreateUserAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var key = "user-" + AuthenticationService.Hash($"{identity.Provider}\n{identity.Issuer}\n{identity.Subject}");
        var doc = new Document
        {
            Id = key, Kind = "user", Provider = identity.Provider, Issuer = identity.Issuer,
            Subject = identity.Subject, UserId = Guid.NewGuid().ToString("N")
        };
        try
        {
            await container.CreateItemAsync(doc, new PartitionKey(key), cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            doc = (await container.ReadItemAsync<Document>(key, new PartitionKey(key), cancellationToken: cancellationToken)).Resource;
        }
        if (doc.Kind != "user" || doc.Provider != identity.Provider || doc.Issuer != identity.Issuer ||
            doc.Subject != identity.Subject || string.IsNullOrEmpty(doc.UserId))
            throw new InvalidOperationException("The stored identity is invalid.");
        return new UserIdentity(doc.UserId, identity);
    }

    public async Task SaveSessionAsync(UserSession session, CancellationToken cancellationToken) =>
        await container.CreateItemAsync(new Document
        {
            Id = "session-" + session.Id, Kind = "session", UserId = session.UserId,
            ExpiresAt = session.ExpiresAt, Ttl = 28800
        }, new PartitionKey("session-" + session.Id), cancellationToken: cancellationToken);

    public async Task<UserSession?> GetSessionAsync(string id, CancellationToken cancellationToken)
    {
        var key = "session-" + id;
        try
        {
            var doc = (await container.ReadItemAsync<Document>(key, new PartitionKey(key), cancellationToken: cancellationToken)).Resource;
            if (doc.Kind != "session" || string.IsNullOrEmpty(doc.UserId))
                throw new InvalidOperationException("The stored session is invalid.");
            return new UserSession(id, doc.UserId, doc.ExpiresAt);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteSessionAsync(string id, CancellationToken cancellationToken)
    {
        var key = "session-" + id;
        try
        {
            await container.DeleteItemAsync<Document>(key, new PartitionKey(key), cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }
}
