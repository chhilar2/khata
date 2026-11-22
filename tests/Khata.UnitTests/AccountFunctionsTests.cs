using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core.Serialization;
using Khata.Api;
using Khata.Application;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Khata.UnitTests;

public class AccountFunctionsTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ValidBody = """{"displayName":" Parent ","contactEmail":"parent@example.com","phone":"+1 555 555 1234","country":"us","currency":"usd"}""";
    private static readonly AuthSettings Settings = new("https://app.example", "client", "AzureCli", null,
        "https://cosmos.example", "database", "container");

    [Fact]
    public async Task GetUsesSessionIdentityAndDoesNotExposePrivateFields()
    {
        var (functions, accounts) = CreateFunctions();
        var now = DateTimeOffset.UtcNow;
        var profile = new AccountProfile("user-one", "google", "google@example.com",
            new AccountFields("Parent", null, null, null, null), now, now);
        accounts.Setup(a => a.GetAsync("user-one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionedAccount(profile, "\"etag\""));
        var response = await functions.Get(CreateRequest(), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control"));
        response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Body);
        Assert.Equal("google@example.com", json.RootElement.GetProperty("email").GetString());
        Assert.Equal("\"etag\"", json.RootElement.GetProperty("version").GetString());
        Assert.False(json.RootElement.TryGetProperty("userId", out _));
        accounts.Verify(a => a.GetAsync("user-one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingSessionAndProfileAreExplicit()
    {
        var (functions, accounts) = CreateFunctions();
        Assert.Equal(HttpStatusCode.Unauthorized, (await functions.Get(CreateRequest(includeCookie: false), default)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await functions.Get(CreateRequest(), default)).StatusCode);
        accounts.Verify(a => a.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateRequiresSessionOriginCsrfAndVersion()
    {
        var (functions, accounts) = CreateFunctions();
        Assert.Equal(HttpStatusCode.Unauthorized, (await functions.Update(CreateRequest(includeCookie: false), default)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await functions.Update(CreateRequest(ValidBody, "https://evil.example"), default)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await functions.Update(CreateRequest(ValidBody, csrf: "wrong"), default)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await functions.Update(CreateRequest(ValidBody, version: "*"), default)).StatusCode);
        accounts.Verify(a => a.UpdateAsync(It.IsAny<string>(), It.IsAny<AccountFields>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateValidatesFieldsAndRejectsInjectedIdentity()
    {
        var (functions, accounts) = CreateFunctions();
        const string invalid = """{"displayName":"","contactEmail":"not-email","phone":"x","country":"XX","currency":"ZZZ"}""";
        var response = await functions.Update(CreateRequest(invalid), default);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Body);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("contactEmail", out _));
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("country", out _));
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("currency", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await functions.Update(
            CreateRequest("""{"displayName":null,"contactEmail":null,"phone":null,"country":null,"userId":"user-two"}"""), default)).StatusCode);
        accounts.Verify(a => a.UpdateAsync(It.IsAny<string>(), It.IsAny<AccountFields>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateNormalizesInputsAndReportsConflicts()
    {
        var (functions, accounts) = CreateFunctions();
        accounts.SetupSequence(a => a.UpdateAsync("user-one", It.IsAny<AccountFields>(), "\"etag\"", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountUpdateResult.Updated).ReturnsAsync(AccountUpdateResult.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await functions.Update(CreateRequest(ValidBody), default)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await functions.Update(CreateRequest(ValidBody), default)).StatusCode);
        accounts.Verify(a => a.UpdateAsync("user-one",
            It.Is<AccountFields>(f => f.DisplayName == "Parent" && f.Country == "US" && f.Currency == "USD"),
            "\"etag\"", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task UpdateCanClearOptionalFields()
    {
        var (functions, accounts) = CreateFunctions();
        accounts.Setup(a => a.UpdateAsync("user-one", It.IsAny<AccountFields>(), "\"etag\"", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountUpdateResult.Updated);
        const string body = """{"displayName":"","contactEmail":null,"phone":"","country":"","currency":""}""";
        Assert.Equal(HttpStatusCode.NoContent, (await functions.Update(CreateRequest(body), default)).StatusCode);
        accounts.Verify(a => a.UpdateAsync("user-one", new AccountFields(null, null, null, null, null),
            "\"etag\"", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (AccountFunctions, Mock<IAccountStore>) CreateFunctions()
    {
        var authStore = new Mock<IAuthenticationStore>();
        authStore.Setup(s => s.GetSessionAsync(AuthenticationService.Hash(Token), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSession(AuthenticationService.Hash(Token), "user-one", DateTimeOffset.UtcNow.AddMinutes(5)));
        var accounts = new Mock<IAccountStore>();
        var authentication = new AuthenticationService(authStore.Object, Mock.Of<IIdentityProvider>(), accounts.Object, TimeProvider.System);
        return (new AccountFunctions(authentication, accounts.Object, Settings), accounts);
    }

    private static HttpRequestData CreateRequest(string body = "", string origin = "https://app.example",
        string? csrf = null, string version = "\"etag\"", bool includeCookie = true)
    {
        var services = new ServiceCollection()
            .Configure<WorkerOptions>(options => options.Serializer = new JsonObjectSerializer())
            .BuildServiceProvider();
        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.InstanceServices).Returns(services);
        var response = new Mock<HttpResponseData>(context.Object);
        response.SetupProperty(r => r.StatusCode);
        response.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection());
        response.SetupGet(r => r.Body).Returns(new MemoryStream());
        var request = new Mock<HttpRequestData>(context.Object);
        request.Setup(r => r.CreateResponse()).Returns(response.Object);
        request.SetupGet(r => r.Cookies).Returns(includeCookie ? [new HttpCookie("__Host-khata-session", Token)] : []);
        var headers = new HttpHeadersCollection();
        headers.Add("Origin", origin);
        headers.Add("X-CSRF-Token", csrf ?? AuthenticationService.CsrfToken(Token));
        headers.Add("If-Match", version);
        request.SetupGet(r => r.Headers).Returns(headers);
        request.SetupGet(r => r.Body).Returns(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        return request.Object;
    }
}
