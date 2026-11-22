using System.Net;
using Khata.Application;
using Khata.Infrastructure;
using Microsoft.Azure.Cosmos;
using Moq;
using Document = Khata.Infrastructure.CosmosAccountStore.Document;

namespace Khata.UnitTests;

public class CosmosAccountStoreTests
{
    private static readonly ValidatedIdentity Identity = new(
        new ExternalIdentity("google", "https://accounts.google.com", "subject"), "Google name", "new@example.com");

    [Fact]
    public async Task FirstSignInCreatesAProfileInUserPartition()
    {
        var container = new Mock<Container>();
        Document? saved = null;
        container.Setup(c => c.CreateItemAsync(It.IsAny<Document>(), It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Document, PartitionKey?, ItemRequestOptions, CancellationToken>((doc, key, _, _) =>
            {
                saved = doc;
                Assert.Equal(new PartitionKey("profile-user"), key);
            })
            .ReturnsAsync(Mock.Of<ItemResponse<Document>>());

        await new CosmosAccountStore(container.Object, TimeProvider.System).EnsureProfileAsync("user", Identity, default);

        Assert.NotNull(saved);
        Assert.Equal("profile-user", saved.Id);
        Assert.Equal(saved.Id, saved.PartitionKey);
        Assert.Equal("Google name", saved.DisplayName);
        Assert.Equal("new@example.com", saved.Email);
        Assert.Equal("google", saved.Provider);
    }

    [Fact]
    public async Task RepeatSignInRetriesConflictsWithoutOverwritingProfileEdits()
    {
        var container = new Mock<Container>();
        container.Setup(c => c.CreateItemAsync(It.IsAny<Document>(), It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Error(HttpStatusCode.Conflict));
        var before = Profile("Old name", "old@example.com");
        var after = Profile("Edited name", "old@example.com");
        container.SetupSequence(c => c.ReadItemAsync<Document>("profile-user", new PartitionKey("profile-user"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(before, "\"v1\"")).ReturnsAsync(Response(after, "\"v2\""));
        var replacements = new List<(string? Name, string? Email, string Version)>();
        container.Setup(c => c.ReplaceItemAsync(It.IsAny<Document>(), "profile-user", It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Document, string, PartitionKey?, ItemRequestOptions, CancellationToken>((doc, _, key, options, _) =>
            {
                Assert.Equal(new PartitionKey("profile-user"), key);
                replacements.Add((doc.DisplayName, doc.Email, options.IfMatchEtag));
            })
            .Returns(() => replacements.Count == 1
                ? Task.FromException<ItemResponse<Document>>(Error(HttpStatusCode.PreconditionFailed))
                : Task.FromResult(Response(after, "\"v3\"")));

        await new CosmosAccountStore(container.Object, TimeProvider.System).EnsureProfileAsync("user", Identity, default);

        Assert.Equal(("Old name", "new@example.com", "\"v1\""), replacements[0]);
        Assert.Equal(("Edited name", "new@example.com", "\"v2\""), replacements[1]);
    }

    [Fact]
    public async Task UpdatesRejectStaleVersionsAndCanClearFields()
    {
        var container = new Mock<Container>();
        var document = Profile("Parent", "google@example.com");
        document.ContactEmail = "old@example.com";
        container.Setup(c => c.ReadItemAsync<Document>("profile-user", new PartitionKey("profile-user"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(document, "\"latest\""));
        container.Setup(c => c.ReplaceItemAsync(It.IsAny<Document>(), "profile-user", It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "\"stale\""), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Error(HttpStatusCode.PreconditionFailed));
        container.Setup(c => c.ReplaceItemAsync(It.IsAny<Document>(), "profile-user", It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "\"latest\""), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(document, "\"next\""));
        var store = new CosmosAccountStore(container.Object, TimeProvider.System);
        var fields = new AccountFields(null, null, null, null, null);

        Assert.Equal(AccountUpdateResult.Conflict, await store.UpdateAsync("user", fields, "\"stale\"", default));
        Assert.Equal(AccountUpdateResult.Updated, await store.UpdateAsync("user", fields, "\"latest\"", default));
        Assert.Null(document.DisplayName);
        Assert.Null(document.ContactEmail);
        Assert.Equal("google@example.com", document.Email);
    }

    private static Document Profile(string name, string email) => new()
    {
        Id = "profile-user", PartitionKey = "profile-user", Kind = "profile", UserId = "user",
        Provider = "google", DisplayName = name, Email = email,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ItemResponse<Document> Response(Document document, string etag)
    {
        var response = new Mock<ItemResponse<Document>>();
        response.SetupGet(r => r.Resource).Returns(document);
        response.SetupGet(r => r.ETag).Returns(etag);
        return response.Object;
    }

    private static CosmosException Error(HttpStatusCode status) => new("Test failure", status, 0, "test", 0);
}
