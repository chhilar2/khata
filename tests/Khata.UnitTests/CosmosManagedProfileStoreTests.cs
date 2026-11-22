using System.Net;
using System.Text.Json;
using Khata.Application;
using Khata.Infrastructure;
using Microsoft.Azure.Cosmos;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Document = Khata.Infrastructure.CosmosManagedProfileStore.Document;
using Receipt = Khata.Infrastructure.CosmosManagedProfileStore.Receipt;
using Balance = Khata.Infrastructure.CosmosManagedProfileStore.BalanceDocument;
using Child = Khata.Infrastructure.CosmosManagedProfileStore.ProfileChild;

namespace Khata.UnitTests;

public class CosmosManagedProfileStoreTests
{
    private static readonly Guid RequestId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly ManagedProfileFields Fields = new("Alex", "2018-01-02");
    private const string ProfileId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task CreateBatchesProfileAndReceiptInOwnerPartition()
    {
        var (container, batch, response) = Batch(HttpStatusCode.OK);
        Receipt? receipt = null;
        Document? document = null;
        Balance? balance = null;
        batch.Setup(b => b.CreateItem(It.IsAny<Receipt>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Callback<Receipt, TransactionalBatchItemRequestOptions>((item, _) => receipt = item).Returns(batch.Object);
        batch.Setup(b => b.CreateItem(It.IsAny<Document>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Callback<Document, TransactionalBatchItemRequestOptions>((item, _) => document = item).Returns(batch.Object);
        batch.Setup(b => b.CreateItem(It.IsAny<Balance>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Callback<Balance, TransactionalBatchItemRequestOptions>((item, _) => balance = item).Returns(batch.Object);
        var operation = new Mock<TransactionalBatchOperationResult>();
        operation.SetupGet(o => o.ETag).Returns("\"v1\"");
        response.Setup(r => r[1]).Returns(operation.Object);

        var result = await new CosmosManagedProfileStore(container.Object, TimeProvider.System)
            .CreateAsync("owner", RequestId, Fields, default);

        Assert.Equal(ProfileCreateStatus.Created, result.Status);
        Assert.NotNull(document);
        Assert.NotNull(receipt);
        Assert.NotNull(balance);
        Assert.Equal(document.Id, receipt.ProfileId);
        Assert.Equal(document.Id, balance.ProfileId);
        Assert.Equal(document.BalanceId, balance.Id);
        Assert.Equal("owner", document.OwnerId);
        Assert.Equal("owner", receipt.OwnerId);
        Assert.Equal(86400, receipt.Ttl);
        Assert.Equal("\"v1\"", result.Profile!.Version);
        container.Verify(c => c.CreateTransactionalBatch(new PartitionKey("owner")), Times.Once);
    }

    [Theory]
    [InlineData("same", ProfileCreateStatus.Replayed)]
    [InlineData("different", ProfileCreateStatus.Conflict)]
    [InlineData("deleted", ProfileCreateStatus.Gone)]
    public async Task DuplicateRequestReplaysConflictsOrReportsDeletion(string scenario, ProfileCreateStatus expected)
    {
        var (container, _, _) = Batch(HttpStatusCode.Conflict);
        var receipt = new Receipt
        {
            Id = "request-" + RequestId.ToString("N"), OwnerId = "owner", Kind = "create-receipt",
            ProfileId = ProfileId,
            Fingerprint = AuthenticationService.Hash(System.Text.Json.JsonSerializer.Serialize(
                scenario == "different" ? new ManagedProfileFields("Other", null) : Fields)),
            Ttl = 86400
        };
        container.Setup(c => c.ReadItemAsync<Receipt>(receipt.Id, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(receipt, "\"receipt\""));
        var read = container.Setup(c => c.ReadItemAsync<Document>(ProfileId, new PartitionKey("owner"),
            It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()));
        if (scenario == "deleted") read.ThrowsAsync(Error(HttpStatusCode.NotFound));
        else read.ReturnsAsync(Item(Profile(), "\"v2\""));

        var result = await new CosmosManagedProfileStore(container.Object, TimeProvider.System)
            .CreateAsync("owner", RequestId, Fields, default);

        Assert.Equal(expected, result.Status);
        Assert.Equal(scenario == "same" ? "\"v2\"" : null, result.Profile?.Version);
    }

    [Fact]
    public async Task BatchFailuresDoNotLookLikeSuccessfulCreation()
    {
        var (container, _, _) = Batch(HttpStatusCode.TooManyRequests);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CosmosManagedProfileStore(container.Object, TimeProvider.System).CreateAsync("owner", RequestId, Fields, default));
    }

    [Fact]
    public async Task ListReadsOnlyOwnersPartitionAndPreservesContinuationAndVersion()
    {
        var container = new Mock<Container>();
        var iterator = new Mock<FeedIterator<Document>>();
        var feed = new Mock<FeedResponse<Document>>();
        var document = Profile();
        document.ETag = "\"v1\"";
        feed.Setup(f => f.GetEnumerator()).Returns(() => ((IEnumerable<Document>)[document]).GetEnumerator());
        feed.SetupGet(f => f.ContinuationToken).Returns("next");
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(feed.Object);
        container.Setup(c => c.GetItemQueryIterator<Document>(It.IsAny<QueryDefinition>(), "previous",
                It.Is<QueryRequestOptions>(options => options.PartitionKey == new PartitionKey("owner") && options.MaxItemCount == 25)))
            .Returns(iterator.Object);

        var page = await new CosmosManagedProfileStore(container.Object, TimeProvider.System).ListAsync("owner", "previous", default);

        Assert.Equal("next", page.ContinuationToken);
        Assert.Equal("\"v1\"", Assert.Single(page.Items).Version);
    }

    [Fact]
    public void CosmosJsonReadsQueryEtagAndWritesReceiptTtl()
    {
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };
        var document = JsonConvert.DeserializeObject<Document>(
            """{"id":"0123456789abcdef0123456789abcdef","ownerId":"owner","kind":"managed-profile","displayName":"Alex","_etag":"\"v1\""}""", settings);
        Assert.Equal("\"v1\"", document!.ETag);
        var receipt = new Receipt
        {
            Id = "request-id", OwnerId = "owner", Kind = "create-receipt", ProfileId = ProfileId,
            Fingerprint = "hash", Ttl = 86400
        };
        using var json = JsonDocument.Parse(JsonConvert.SerializeObject(receipt, settings));
        Assert.Equal(86400, json.RootElement.GetProperty("ttl").GetInt32());
        Assert.Equal("owner", json.RootElement.GetProperty("ownerId").GetString());
    }

    [Fact]
    public async Task UpdateAndDeleteUseOwnerPartitionAndConditionalEtag()
    {
        var container = new Mock<Container>();
        container.Setup(c => c.ReadItemAsync<Document>(ProfileId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(Profile(), "\"latest\""));
        container.Setup(c => c.ReplaceItemAsync(It.IsAny<Document>(), ProfileId, new PartitionKey("owner"),
                It.Is<ItemRequestOptions>(o => o.IfMatchEtag == "\"stale\""), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Error(HttpStatusCode.PreconditionFailed));
        container.Setup(c => c.ReadItemAsync<Document>(ProfileId, new PartitionKey("other-owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Error(HttpStatusCode.NotFound));
        var store = new CosmosManagedProfileStore(container.Object, TimeProvider.System);

        Assert.Equal(ProfileWriteStatus.Conflict, (await store.UpdateAsync("owner", ProfileId, Fields, "\"stale\"", default)).Status);
        Assert.Equal(ProfileWriteStatus.Missing, (await store.DeleteAsync("other-owner", ProfileId, "\"v1\"", default)).Status);
    }

    [Fact]
    public async Task DeleteMarksProfileAndRemovesChildrenWithAConditionalBatch()
    {
        var (container, batch, response) = Batch(HttpStatusCode.OK);
        var profile = Profile();
        container.Setup(c => c.ReadItemAsync<Document>(ProfileId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(profile, "\"v1\""));
        container.Setup(c => c.ReplaceItemAsync(profile, ProfileId, new PartitionKey("owner"),
                It.Is<ItemRequestOptions>(o => o.IfMatchEtag == "\"v1\""), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(profile, "\"marked\""));
        var iterator = Children("balance-id");
        container.Setup(c => c.GetItemQueryIterator<Child>(It.IsAny<QueryDefinition>(), null,
                It.Is<QueryRequestOptions>(o => o.PartitionKey == new PartitionKey("owner") && o.MaxItemCount == 50)))
            .Returns(iterator.Object);
        batch.Setup(b => b.ReplaceItem(ProfileId, profile,
                It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "\"marked\"")))
            .Returns(batch.Object);
        batch.Setup(b => b.DeleteItem("balance-id", It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(batch.Object);
        var operation = new Mock<TransactionalBatchOperationResult>();
        operation.SetupGet(o => o.ETag).Returns("\"pending\"");
        response.Setup(r => r[0]).Returns(operation.Object);

        var result = await new CosmosManagedProfileStore(container.Object, TimeProvider.System)
            .DeleteAsync("owner", ProfileId, "\"v1\"", default);

        Assert.Equal(ProfileWriteStatus.DeletionPending, result.Status);
        Assert.True(result.Profile!.DeletionPending);
        Assert.Equal("\"pending\"", result.Profile.Version);
        batch.Verify(b => b.DeleteItem("balance-id", It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Once);
    }

    [Fact]
    public async Task DeleteRemovesCreationReceiptAndProfileInOneBatch()
    {
        var (container, batch, _) = Batch(HttpStatusCode.OK);
        var profile = Profile();
        profile.Deleting = true;
        container.Setup(c => c.ReadItemAsync<Document>(ProfileId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(profile, "\"pending\""));
        container.SetupSequence(c => c.GetItemQueryIterator<Child>(It.IsAny<QueryDefinition>(), null,
                It.IsAny<QueryRequestOptions>()))
            .Returns(Children().Object)
            .Returns(Children("request-id").Object);
        batch.Setup(b => b.DeleteItem("request-id", It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Returns(batch.Object);
        batch.Setup(b => b.DeleteItem(ProfileId, It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "\"pending\"")))
            .Returns(batch.Object);

        var result = await new CosmosManagedProfileStore(container.Object, TimeProvider.System)
            .DeleteAsync("owner", ProfileId, "\"pending\"", default);

        Assert.Equal(ProfileWriteStatus.Deleted, result.Status);
        batch.Verify(b => b.DeleteItem("request-id", It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Once);
        batch.Verify(b => b.DeleteItem(ProfileId, It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "\"pending\"")), Times.Once);
    }

    private static Mock<FeedIterator<Child>> Children(params string[] ids)
    {
        var iterator = new Mock<FeedIterator<Child>>();
        var feed = new Mock<FeedResponse<Child>>();
        var children = ids.Select(id => new Child { Id = id }).ToArray();
        feed.Setup(f => f.GetEnumerator()).Returns(() => ((IEnumerable<Child>)children).GetEnumerator());
        iterator.SetupSequence(i => i.HasMoreResults).Returns(true).Returns(false);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(feed.Object);
        return iterator;
    }

    private static (Mock<Container>, Mock<TransactionalBatch>, Mock<TransactionalBatchResponse>) Batch(HttpStatusCode status)
    {
        var container = new Mock<Container>();
        var batch = new Mock<TransactionalBatch>();
        var response = new Mock<TransactionalBatchResponse>();
        container.Setup(c => c.CreateTransactionalBatch(new PartitionKey("owner"))).Returns(batch.Object);
        batch.Setup(b => b.CreateItem(It.IsAny<Receipt>(), It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(batch.Object);
        batch.Setup(b => b.CreateItem(It.IsAny<Document>(), It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(batch.Object);
        batch.Setup(b => b.CreateItem(It.IsAny<Balance>(), It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(batch.Object);
        batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);
        response.SetupGet(r => r.StatusCode).Returns(status);
        response.SetupGet(r => r.IsSuccessStatusCode).Returns(status == HttpStatusCode.OK);
        return (container, batch, response);
    }

    private static Document Profile() => new()
    {
        Id = ProfileId, OwnerId = "owner", Kind = "managed-profile",
        DisplayName = "Alex", DateOfBirth = "2018-01-02",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ItemResponse<T> Item<T>(T document, string etag)
    {
        var response = new Mock<ItemResponse<T>>();
        response.SetupGet(r => r.Resource).Returns(document);
        response.SetupGet(r => r.ETag).Returns(etag);
        return response.Object;
    }

    private static CosmosException Error(HttpStatusCode status) => new("Test failure", status, 0, "test", 0);
}
