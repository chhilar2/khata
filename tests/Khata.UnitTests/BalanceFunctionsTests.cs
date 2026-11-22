using System.Net;
using System.Text.Json;
using Khata.Api;
using Khata.Application;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Khata.UnitTests;

public class BalanceFunctionsTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ProfileId = "0123456789abcdef0123456789abcdef";
    private const string BalanceId = "11111111111111111111111111111111";
    private static readonly AuthSettings Settings = new("https://app.example", "client", "AzureCli", null,
        "https://cosmos.example", "database", "container");

    [Fact]
    public async Task ProfileBalanceIsOwnerScopedAndDoesNotClaimAnAvailableNetWorth()
    {
        var (functions, store) = Create();
        store.Setup(s => s.GetOrCreateForProfileAsync("user-one", ProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Balance(BalanceId, ProfileId, "USD", 10m, "\"v1\""));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await functions.GetProfileBalance(ManagedProfileFunctionsTests.Request(token: null), ProfileId, default)).StatusCode);
        var response = await functions.GetProfileBalance(ManagedProfileFunctionsTests.Request(), ProfileId, default);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Body);
        Assert.False(json.RootElement.GetProperty("netWorthAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("netWorth").ValueKind);
        Assert.Equal("10.00", json.RootElement.GetProperty("cash").GetString());
        store.Verify(s => s.GetOrCreateForProfileAsync("user-one", ProfileId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CurrencyUpdateRequiresOriginCsrfVersionAndUsd()
    {
        var (functions, store) = Create();
        const string body = """{"currency":"USD"}""";
        Assert.Equal(HttpStatusCode.Forbidden, (await functions.SetCurrency(
            ManagedProfileFunctionsTests.Request(body, csrf: "bad"), BalanceId, default)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await functions.SetCurrency(
            ManagedProfileFunctionsTests.Request(body, version: "*"), BalanceId, default)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await functions.SetCurrency(
            ManagedProfileFunctionsTests.Request("""{"currency":"EUR"}"""), BalanceId, default)).StatusCode);
        store.Setup(s => s.SetCurrencyAsync("user-one", BalanceId, "USD", "\"v1\"", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceUpdateResult(BalanceUpdateStatus.Conflict));
        Assert.Equal(HttpStatusCode.Conflict, (await functions.SetCurrency(
            ManagedProfileFunctionsTests.Request(body), BalanceId, default)).StatusCode);
        store.Verify(s => s.SetCurrencyAsync("user-one", BalanceId, "USD", "\"v1\"", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CashAdjustmentIsIdempotentAndOwnerScoped()
    {
        var (functions, store) = Create();
        const string body = """{"operation":"deposit","amount":"2.50"}""";
        const string requestId = "01234567-89ab-cdef-0123-456789abcdef";
        var entry = new LedgerEntry("22222222222222222222222222222222", BalanceId, "deposit", 2.50m, 7.50m, DateTimeOffset.UtcNow);
        store.Setup(s => s.AdjustCashAsync("user-one", BalanceId, Guid.Parse(requestId), "deposit", 2.50m, "\"v1\"", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LedgerWriteResult(LedgerWriteStatus.Replayed, entry));

        var response = await functions.AdjustCash(ManagedProfileFunctionsTests.Request(body), BalanceId, default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"/api/balances/{BalanceId}/ledger/{entry.Id}", response.Headers.GetValues("Location").Single());
        response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Body);
        Assert.Equal("2.50", json.RootElement.GetProperty("amount").GetString());
        Assert.Equal("7.50", json.RootElement.GetProperty("cashAfter").GetString());
        store.Verify(s => s.AdjustCashAsync("user-one", BalanceId, Guid.Parse(requestId), "deposit", 2.50m, "\"v1\"", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FinancialWritesAreDisabledByDefault()
    {
        var (functions, store) = Create(enabled: false);
        var response = await functions.AdjustCash(ManagedProfileFunctionsTests.Request("""{"operation":"deposit","amount":"1.00"}"""), BalanceId, default);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        store.Verify(s => s.AdjustCashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (BalanceFunctions, Mock<IBalanceStore>) Create(bool enabled = true)
    {
        var authStore = new Mock<IAuthenticationStore>();
        authStore.Setup(s => s.GetSessionAsync(AuthenticationService.Hash(Token), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSession(AuthenticationService.Hash(Token), "user-one", DateTimeOffset.UtcNow.AddMinutes(5)));
        var authentication = new AuthenticationService(authStore.Object, Mock.Of<IIdentityProvider>(),
            Mock.Of<IAccountStore>(), TimeProvider.System);
        var balances = new Mock<IBalanceStore>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(enabled
            ? new Dictionary<string, string?> { ["FinancialFeatures:Enabled"] = "true" }
            : new Dictionary<string, string?>()).Build();
        return (new BalanceFunctions(authentication, balances.Object, Settings, configuration), balances);
    }
}
