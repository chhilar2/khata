using System.Net;
using Khata.Application;
using Khata.Infrastructure;
using Microsoft.Azure.Cosmos;
using Moq;
using BalanceDocument = Khata.Infrastructure.CosmosManagedProfileStore.BalanceDocument;
using ProfileDocument = Khata.Infrastructure.CosmosManagedProfileStore.Document;
using Receipt = Khata.Infrastructure.CosmosBalanceStore.FinancialReceipt;
using LedgerDocument = Khata.Infrastructure.CosmosBalanceStore.LedgerDocument;
using InvestmentDocument = Khata.Infrastructure.CosmosBalanceStore.InvestmentDocument;
using GigDocument = Khata.Infrastructure.CosmosBalanceStore.GigDocument;
using MutationReceipt = Khata.Infrastructure.CosmosBalanceStore.MutationReceipt;

namespace Khata.UnitTests;

public class CosmosBalanceStoreTests
{
    private const string ProfileId = "0123456789abcdef0123456789abcdef";
    private const string BalanceId = "11111111111111111111111111111111";

    [Fact]
    public async Task LegacyProfileInitializationCreatesOneBalanceInOwnersPartition()
    {
        var container = new Mock<Container>();
        var profile = Profile(balanceId: null);
        container.Setup(c => c.ReadItemAsync<ProfileDocument>(ProfileId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(profile, "\"profile\""));
        var batch = new Mock<TransactionalBatch>();
        var response = BatchResponse(HttpStatusCode.OK, "\"balance\"");
        container.Setup(c => c.CreateTransactionalBatch(new PartitionKey("owner"))).Returns(batch.Object);
        batch.Setup(b => b.ReplaceItem(ProfileId, profile,
                It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "\"profile\""))).Returns(batch.Object);
        BalanceDocument? saved = null;
        batch.Setup(b => b.CreateItem(It.IsAny<BalanceDocument>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Callback<BalanceDocument, TransactionalBatchItemRequestOptions>((doc, _) => saved = doc)
            .Returns(batch.Object);
        batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);

        var balance = await new CosmosBalanceStore(container.Object, TimeProvider.System).GetOrCreateForProfileAsync("owner", ProfileId, default);

        Assert.NotNull(balance);
        Assert.NotNull(saved);
        Assert.Equal(saved.Id, profile.BalanceId);
        Assert.Equal(ProfileId, saved.ProfileId);
        Assert.Equal("owner", saved.OwnerId);
        Assert.Null(balance.Currency);
        Assert.Equal(0m, balance.Cash);
        Assert.Equal("\"balance\"", balance.Version);
    }

    [Fact]
    public async Task BalanceReadsRequireMatchingActiveOwnedProfile()
    {
        var container = new Mock<Container>();
        var doc = new BalanceDocument { Id = BalanceId, OwnerId = "owner", ProfileId = ProfileId, CashCents = 1234 };
        var profile = Profile(BalanceId);
        container.Setup(c => c.ReadItemAsync<BalanceDocument>(BalanceId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(doc, "\"v1\""));
        container.Setup(c => c.ReadItemAsync<ProfileDocument>(ProfileId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(profile, "\"profile\""));
        container.Setup(c => c.ReadItemAsync<BalanceDocument>(BalanceId, new PartitionKey("other"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Error(HttpStatusCode.NotFound));
        var store = new CosmosBalanceStore(container.Object, TimeProvider.System);

        Assert.Equal(12.34m, (await store.GetAsync("owner", BalanceId, default))!.Cash);
        Assert.Null(await store.GetAsync("other", BalanceId, default));
        profile.Deleting = true;
        Assert.Null(await store.GetAsync("owner", BalanceId, default));
    }

    [Fact]
    public async Task CurrencyUpdateConflictsIfProfileChangesDuringTransaction()
    {
        var container = new Mock<Container>();
        var profile = Profile(BalanceId);
        var doc = new BalanceDocument { Id = BalanceId, OwnerId = "owner", ProfileId = ProfileId };
        container.Setup(c => c.ReadItemAsync<BalanceDocument>(BalanceId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(doc, "\"balance\""));
        container.Setup(c => c.ReadItemAsync<ProfileDocument>(ProfileId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(profile, "\"profile\""));
        var batch = new Mock<TransactionalBatch>();
        container.Setup(c => c.CreateTransactionalBatch(new PartitionKey("owner"))).Returns(batch.Object);
        batch.Setup(b => b.ReplaceItem(ProfileId, profile,
            It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "\"profile\""))).Returns(batch.Object);
        batch.Setup(b => b.ReplaceItem(BalanceId, doc,
            It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "\"balance\""))).Returns(batch.Object);
        batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(BatchResponse(HttpStatusCode.PreconditionFailed, null).Object);

        var result = await new CosmosBalanceStore(container.Object, TimeProvider.System).SetCurrencyAsync("owner", BalanceId, "USD", "\"balance\"", default);

        Assert.Equal(BalanceUpdateStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task CashAdjustmentAtomicallyUpdatesBalanceAndCreatesEntryAndReceipt()
    {
        var container = new Mock<Container>();
        var profile = Profile(BalanceId);
        var balance = new BalanceDocument { Id = BalanceId, OwnerId = "owner", ProfileId = ProfileId, Currency = "USD", CashCents = 1000 };
        var requestId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        container.Setup(c => c.ReadItemAsync<Receipt>("financial-request-" + requestId.ToString("N"), new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Error(HttpStatusCode.NotFound));
        container.Setup(c => c.ReadItemAsync<BalanceDocument>(BalanceId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(balance, "\"balance\""));
        container.Setup(c => c.ReadItemAsync<ProfileDocument>(ProfileId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(profile, "\"profile\""));
        var batch = new Mock<TransactionalBatch>();
        container.Setup(c => c.CreateTransactionalBatch(new PartitionKey("owner"))).Returns(batch.Object);
        batch.Setup(b => b.ReplaceItem(ProfileId, profile, It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "\"profile\"")))
            .Returns(batch.Object);
        batch.Setup(b => b.ReplaceItem(BalanceId, balance, It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "\"balance\"")))
            .Returns(batch.Object);
        LedgerDocument? entry = null;
        Receipt? receipt = null;
        batch.Setup(b => b.CreateItem(It.IsAny<LedgerDocument>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Callback<LedgerDocument, TransactionalBatchItemRequestOptions>((item, _) => entry = item).Returns(batch.Object);
        batch.Setup(b => b.CreateItem(It.IsAny<Receipt>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Callback<Receipt, TransactionalBatchItemRequestOptions>((item, _) => receipt = item).Returns(batch.Object);
        batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(BatchResponse(HttpStatusCode.OK, null).Object);

        var result = await new CosmosBalanceStore(container.Object, TimeProvider.System)
            .AdjustCashAsync("owner", BalanceId, requestId, "withdrawal", 2.50m, "\"balance\"", default);

        Assert.Equal(LedgerWriteStatus.Created, result.Status);
        Assert.Equal(7.50m, result.Entry!.CashAfter);
        Assert.Equal(-250, entry!.AmountCents);
        Assert.Equal(entry.Id, receipt!.Entry.Id);
        Assert.Equal(ProfileId, receipt.ProfileId);
        Assert.Equal(750, balance.CashCents);
    }

    [Fact]
    public async Task SameIdempotencyKeyReplaysSnapshotAndRejectsDifferentAmount()
    {
        var container = new Mock<Container>();
        var requestId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        const string operation = "deposit";
        const long cents = 500;
        var receipt = new Receipt
        {
            Id = "financial-request-" + requestId.ToString("N"), OwnerId = "owner", ProfileId = ProfileId,
            BalanceId = BalanceId, Fingerprint = AuthenticationService.Hash(System.Text.Json.JsonSerializer.Serialize(new { balanceId = BalanceId, operation, cents })),
            Entry = new LedgerDocument { Id = "entry", OwnerId = "owner", ProfileId = ProfileId, BalanceId = BalanceId,
                Operation = operation, AmountCents = cents, CashAfterCents = 1500, CreatedAt = DateTimeOffset.UtcNow }
        };
        container.Setup(c => c.ReadItemAsync<Receipt>(receipt.Id, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(receipt, "\"receipt\""));
        var store = new CosmosBalanceStore(container.Object, TimeProvider.System);

        var replay = await store.AdjustCashAsync("owner", BalanceId, requestId, operation, 5m, "\"old\"", default);
        var conflict = await store.AdjustCashAsync("owner", BalanceId, requestId, operation, 6m, "\"old\"", default);

        Assert.Equal(LedgerWriteStatus.Replayed, replay.Status);
        Assert.Equal(15m, replay.Entry!.CashAfter);
        Assert.Equal(LedgerWriteStatus.Conflict, conflict.Status);
        container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
    }

    [Fact]
    public async Task RetryDetectsReceiptCommittedAfterInitialLookup()
    {
        var container = new Mock<Container>();
        var requestId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        const string operation = "deposit";
        const long cents = 500;
        var receipt = new Receipt
        {
            Id = "financial-request-" + requestId.ToString("N"), OwnerId = "owner", ProfileId = ProfileId,
            BalanceId = BalanceId, Fingerprint = AuthenticationService.Hash(System.Text.Json.JsonSerializer.Serialize(new { balanceId = BalanceId, operation, cents })),
            Entry = new LedgerDocument { Id = "entry", OwnerId = "owner", ProfileId = ProfileId, BalanceId = BalanceId,
                Operation = operation, AmountCents = cents, CashAfterCents = 1500, CreatedAt = DateTimeOffset.UtcNow }
        };
        container.SetupSequence(c => c.ReadItemAsync<Receipt>(receipt.Id, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Error(HttpStatusCode.NotFound)).ReturnsAsync(Item(receipt, "\"receipt\""));
        container.Setup(c => c.ReadItemAsync<BalanceDocument>(BalanceId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(new BalanceDocument { Id = BalanceId, OwnerId = "owner", ProfileId = ProfileId, Currency = "USD" }, "\"new\""));
        container.Setup(c => c.ReadItemAsync<ProfileDocument>(ProfileId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Item(Profile(BalanceId), "\"profile\""));

        var replay = await new CosmosBalanceStore(container.Object, TimeProvider.System)
            .AdjustCashAsync("owner", BalanceId, requestId, operation, 5m, "\"old\"", default);

        Assert.Equal(LedgerWriteStatus.Replayed, replay.Status);
        Assert.Equal(15m, replay.Entry!.CashAfter);
        container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
    }

    private const string InvestmentId = "22222222222222222222222222222222";
    private static readonly Guid RequestId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

    private static Mock<Container> TransitionContainer(BalanceDocument balance, InvestmentDocument investment,
        MutationReceipt? receipt = null)
    {
        var container = new Mock<Container>();
        var receiptId = "transition-request-" + RequestId.ToString("N");
        if (receipt is null)
            container.Setup(c => c.ReadItemAsync<MutationReceipt>(receiptId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(Error(HttpStatusCode.NotFound));
        else
            container.Setup(c => c.ReadItemAsync<MutationReceipt>(receiptId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(Item(receipt, "\"receipt\""));
        container.Setup(c => c.ReadItemAsync<BalanceDocument>(BalanceId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(Item(balance, "\"balance\""));
        container.Setup(c => c.ReadItemAsync<ProfileDocument>(ProfileId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(Item(Profile(BalanceId), "\"profile\""));
        container.Setup(c => c.ReadItemAsync<InvestmentDocument>(InvestmentId, new PartitionKey("owner"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(Item(investment, "\"investment\""));
        return container;
    }

    private static InvestmentDocument Unfunded() => new()
    {
        Id = InvestmentId, OwnerId = "owner", ProfileId = ProfileId, BalanceId = BalanceId,
        AnnualRatePercent = 5m, Compounding = "annually"
    };

    [Fact]
    public async Task TransitionFundsInvestmentWithLedgerEntryAndFullOutcomeReceiptInOneBatch()
    {
        var balance = new BalanceDocument { Id = BalanceId, OwnerId = "owner", ProfileId = ProfileId, Currency = "USD", CashCents = 10_000 };
        var container = TransitionContainer(balance, Unfunded());
        var batch = new Mock<TransactionalBatch>();
        container.Setup(c => c.CreateTransactionalBatch(new PartitionKey("owner"))).Returns(batch.Object);
        batch.Setup(b => b.ReplaceItem(It.IsAny<string>(), It.IsAny<ProfileDocument>(), It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(batch.Object);
        batch.Setup(b => b.ReplaceItem(BalanceId, balance, It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(batch.Object);
        batch.Setup(b => b.ReplaceItem(InvestmentId, It.IsAny<InvestmentDocument>(), It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(batch.Object);
        LedgerDocument? entry = null;
        MutationReceipt? receipt = null;
        batch.Setup(b => b.CreateItem(It.IsAny<LedgerDocument>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Callback<LedgerDocument, TransactionalBatchItemRequestOptions>((item, _) => entry = item).Returns(batch.Object);
        batch.Setup(b => b.CreateItem(It.IsAny<MutationReceipt>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
            .Callback<MutationReceipt, TransactionalBatchItemRequestOptions>((item, _) => receipt = item).Returns(batch.Object);
        var response = BatchResponse(HttpStatusCode.OK, "\"balance2\"");
        var destination = new Mock<TransactionalBatchOperationResult>();
        destination.SetupGet(o => o.ETag).Returns("\"investment2\"");
        response.Setup(r => r[2]).Returns(destination.Object);
        batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);

        var result = await new CosmosBalanceStore(container.Object, TimeProvider.System).TransitionAsync(
            "owner", BalanceId, RequestId, "cash", InvestmentId, 25m, "\"unused\"", "\"investment\"", "\"balance\"", default);

        Assert.Equal(TransitionStatus.Updated, result.Status);
        Assert.Equal(75m, result.Cash);
        Assert.Equal(InvestmentState.Active, result.Destination!.State);
        Assert.Equal(-2500, entry!.AmountCents);
        Assert.Equal(InvestmentId, entry.SourceId);
        Assert.Equal(1, balance.ReceiptCount);
        Assert.Contains("\"Cash\":75", receipt!.Outcome);
    }

    [Fact]
    public async Task TransitionReplayReturnsSnapshotAndDifferentPayloadConflicts()
    {
        var balance = new BalanceDocument { Id = BalanceId, OwnerId = "owner", ProfileId = ProfileId, Currency = "USD", CashCents = 7_500 };
        var funded = Unfunded();
        funded.State = "active";
        funded.PrincipalCents = 2_500;
        funded.FundedOn = new DateOnly(2026, 1, 1);
        var snapshot = new TransitionResult(TransitionStatus.Updated, 75m, "snapshot",
            Destination: new Investment(InvestmentId, BalanceId, InvestmentKind.FixedRate, InvestmentState.Active, 5m,
                CompoundingModel.Annually, 25m, new DateOnly(2026, 1, 1), null, null, "snapshot"));
        var receipt = new MutationReceipt
        {
            Id = "transition-request-" + RequestId.ToString("N"), OwnerId = "owner", ProfileId = ProfileId, BalanceId = BalanceId,
            Fingerprint = AuthenticationService.Hash(System.Text.Json.JsonSerializer.Serialize(new
                { balanceId = BalanceId, source = "cash", destination = InvestmentId, amountCents = 2500L })),
            ResourceType = "transition", ResourceId = InvestmentId, Outcome = System.Text.Json.JsonSerializer.Serialize(snapshot)
        };
        var store = new CosmosBalanceStore(TransitionContainer(balance, funded, receipt).Object, TimeProvider.System);

        var replay = await store.TransitionAsync("owner", BalanceId, RequestId, "cash", InvestmentId, 25m, "\"old\"", "\"old\"", "\"old\"", default);
        var conflict = await store.TransitionAsync("owner", BalanceId, RequestId, "cash", InvestmentId, 30m, "\"old\"", "\"old\"", "\"old\"", default);

        Assert.Equal(TransitionStatus.Replayed, replay.Status);
        Assert.Equal(75m, replay.Cash);
        Assert.Equal("\"balance\"", replay.BalanceVersion);
        Assert.Equal("\"investment\"", replay.Destination!.Version);
        Assert.Equal(TransitionStatus.Conflict, conflict.Status);
    }

    [Fact]
    public async Task MutationsAreRejectedAtReceiptLimitWithoutWriting()
    {
        var balance = new BalanceDocument { Id = BalanceId, OwnerId = "owner", ProfileId = ProfileId, Currency = "USD", CashCents = 10_000, ReceiptCount = 10_000 };
        var container = TransitionContainer(balance, Unfunded());
        var store = new CosmosBalanceStore(container.Object, TimeProvider.System);

        var result = await store.TransitionAsync("owner", BalanceId, RequestId, "cash", InvestmentId, 25m, "\"x\"", "\"investment\"", "\"balance\"", default);

        Assert.Equal(TransitionStatus.ReceiptLimitReached, result.Status);
        container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
    }

    [Fact]
    public async Task GigStaleVersionConflictsAndMissingGigIsNotFound()
    {
        var balance = new BalanceDocument { Id = BalanceId, OwnerId = "owner", ProfileId = ProfileId, Currency = "USD" };
        var container = TransitionContainer(balance, Unfunded());
        var gigId = "33333333333333333333333333333333";
        container.Setup(c => c.ReadItemAsync<MutationReceipt>("gig-request-" + RequestId.ToString("N"), new PartitionKey("owner"),
            It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(Error(HttpStatusCode.NotFound));
        container.Setup(c => c.ReadItemAsync<GigDocument>(gigId, new PartitionKey("owner"),
            It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(Item(new GigDocument
        {
            Id = gigId, OwnerId = "owner", ProfileId = ProfileId, BalanceId = BalanceId, Name = "Chores",
            StartDate = new DateOnly(2026, 1, 1), PayoutCents = 500, State = "planned"
        }, "\"gig\""));
        container.Setup(c => c.ReadItemAsync<GigDocument>("missing", new PartitionKey("owner"),
            It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(Error(HttpStatusCode.NotFound));
        var store = new CosmosBalanceStore(container.Object, TimeProvider.System);

        var stale = await store.UpdateGigAsync("owner", BalanceId, gigId, RequestId, "Chores", null,
            new DateOnly(2026, 1, 1), 5m, GigState.Completed, "\"old\"", default);
        var missing = await store.UpdateGigAsync("owner", BalanceId, "missing", RequestId, "Chores", null,
            new DateOnly(2026, 1, 1), 5m, GigState.Completed, "\"gig\"", default);

        Assert.Equal(GigWriteStatus.Conflict, stale.Status);
        Assert.Equal(GigWriteStatus.Missing, missing.Status);
        container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
    }

    private static ProfileDocument Profile(string? balanceId) => new()
    {
        Id = ProfileId, OwnerId = "owner", Kind = "managed-profile", DisplayName = "Alex",
        BalanceId = balanceId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ItemResponse<T> Item<T>(T value, string etag)
    {
        var result = new Mock<ItemResponse<T>>();
        result.SetupGet(r => r.Resource).Returns(value);
        result.SetupGet(r => r.ETag).Returns(etag);
        return result.Object;
    }

    private static Mock<TransactionalBatchResponse> BatchResponse(HttpStatusCode status, string? etag)
    {
        var response = new Mock<TransactionalBatchResponse>();
        response.SetupGet(r => r.StatusCode).Returns(status);
        response.SetupGet(r => r.IsSuccessStatusCode).Returns(status == HttpStatusCode.OK);
        var operation = new Mock<TransactionalBatchOperationResult>();
        if (etag is not null) operation.SetupGet(o => o.ETag).Returns(etag);
        response.Setup(r => r[1]).Returns(operation.Object);
        return response;
    }

    private static CosmosException Error(HttpStatusCode status) => new("Test failure", status, 0, "test", 0);
}
