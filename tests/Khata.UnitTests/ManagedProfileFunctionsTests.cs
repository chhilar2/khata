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

public class ManagedProfileFunctionsTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherToken = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ProfileId = "0123456789abcdef0123456789abcdef";
    private const string RequestId = "01234567-89ab-cdef-0123-456789abcdef";
    private const string Body = """{"displayName":"  Alex  ","dateOfBirth":"2018-01-02"}""";
    private static readonly AuthSettings Settings = new("https://app.example", "client", "AzureCli", null,
        "https://cosmos.example", "database", "container");
    private static readonly ManagedProfile Profile = new(ProfileId, "Alex", "2018-01-02",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "\"v1\"");

    [Fact]
    public async Task CreateRequiresSessionOriginCsrfAndRequestId()
    {
        var (functions, store) = CreateFunctions();
        Assert.Equal(HttpStatusCode.Unauthorized, (await functions.Create(Request(token: null), default)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await functions.Create(Request(origin: "https://evil.example"), default)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await functions.Create(Request(csrf: "wrong"), default)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await functions.Create(Request(requestId: "bad"), default)).StatusCode);
        store.Verify(s => s.CreateAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<ManagedProfileFields>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateValidatesInputAndRejectsClientOwnership()
    {
        var (functions, store) = CreateFunctions();
        var invalid = await functions.Create(Request("""{"displayName":" ","dateOfBirth":"9999-12-31"}"""), default);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        invalid.Body.Position = 0;
        using var errors = await JsonDocument.ParseAsync(invalid.Body);
        Assert.True(errors.RootElement.GetProperty("errors").TryGetProperty("displayName", out _));
        Assert.True(errors.RootElement.GetProperty("errors").TryGetProperty("dateOfBirth", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await functions.Create(
            Request("""{"displayName":"Alex","dateOfBirth":null,"ownerId":"other"}"""), default)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await functions.Create(
            Request("""{"displayName":"Alex","dateOfBirth":"2024-02-30"}"""), default)).StatusCode);
        store.Verify(s => s.CreateAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<ManagedProfileFields>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAcceptsOmittedOptionalDateOfBirth()
    {
        var (functions, store) = CreateFunctions();
        store.Setup(s => s.CreateAsync("user-one", Guid.Parse(RequestId),
                new ManagedProfileFields("Alex", null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileCreateResult(ProfileCreateStatus.Created, Profile with { DateOfBirth = null }));

        var response = await functions.Create(Request("""{"displayName":"Alex"}"""), default);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(ProfileCreateStatus.Created, HttpStatusCode.Created)]
    [InlineData(ProfileCreateStatus.Replayed, HttpStatusCode.OK)]
    [InlineData(ProfileCreateStatus.Conflict, HttpStatusCode.Conflict)]
    [InlineData(ProfileCreateStatus.Gone, HttpStatusCode.Gone)]
    public async Task CreateHonorsIdempotencyResult(ProfileCreateStatus result, HttpStatusCode status)
    {
        var (functions, store) = CreateFunctions();
        store.Setup(s => s.CreateAsync("user-one", Guid.Parse(RequestId),
                new ManagedProfileFields("Alex", "2018-01-02"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileCreateResult(result, result is ProfileCreateStatus.Created or ProfileCreateStatus.Replayed ? Profile : null));
        var response = await functions.Create(Request(), default);
        Assert.Equal(status, response.StatusCode);
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control"));
        if (result is ProfileCreateStatus.Created or ProfileCreateStatus.Replayed)
        {
            Assert.Equal("/api/managed-profiles/" + ProfileId, response.Headers.GetValues("Location").Single());
            response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(response.Body);
            Assert.Equal(ProfileId, json.RootElement.GetProperty("id").GetString());
            Assert.Equal("\"v1\"", json.RootElement.GetProperty("version").GetString());
            Assert.False(json.RootElement.TryGetProperty("ownerId", out _));
        }
    }

    [Fact]
    public async Task ReadsAreScopedToSessionOwner()
    {
        var (functions, store) = CreateFunctions();
        store.Setup(s => s.GetAsync("user-one", ProfileId, It.IsAny<CancellationToken>())).ReturnsAsync(Profile);
        store.Setup(s => s.ListAsync("user-two", "next", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedProfilePage([], null));

        Assert.Equal(HttpStatusCode.OK, (await functions.Get(Request(), ProfileId, default)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await functions.Get(Request(token: OtherToken), ProfileId, default)).StatusCode);
        var page = await functions.List(Request(token: OtherToken, query: "?continuationToken=next"), default);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        page.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(page.Body);
        Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        store.Verify(s => s.GetAsync("user-two", ProfileId, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.ListAsync("user-two", "next", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WritesAreOwnerScopedAndRespectVersionAndMissingStatus()
    {
        var (functions, store) = CreateFunctions();
        store.Setup(s => s.UpdateAsync("user-one", ProfileId, It.IsAny<ManagedProfileFields>(), "\"v1\"", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileWriteResult(ProfileWriteStatus.Conflict));
        store.Setup(s => s.DeleteAsync("user-two", ProfileId, "\"v1\"", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileWriteResult(ProfileWriteStatus.Missing));
        Assert.Equal(HttpStatusCode.Conflict, (await functions.Update(Request(), ProfileId, default)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await functions.Delete(Request(token: OtherToken), ProfileId, default)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await functions.Delete(Request(version: "*"), ProfileId, default)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await functions.Delete(Request(csrf: "wrong"), ProfileId, default)).StatusCode);
        store.Verify(s => s.DeleteAsync("user-two", ProfileId, "\"v1\"", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (ManagedProfileFunctions, Mock<IManagedProfileStore>) CreateFunctions()
    {
        var authStore = new Mock<IAuthenticationStore>();
        foreach (var (token, user) in new[] { (Token, "user-one"), (OtherToken, "user-two") })
            authStore.Setup(s => s.GetSessionAsync(AuthenticationService.Hash(token), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UserSession(AuthenticationService.Hash(token), user, DateTimeOffset.UtcNow.AddMinutes(5)));
        var authentication = new AuthenticationService(authStore.Object, Mock.Of<IIdentityProvider>(),
            Mock.Of<IAccountStore>(), TimeProvider.System);
        var profiles = new Mock<IManagedProfileStore>();
        return (new ManagedProfileFunctions(authentication, profiles.Object, Settings, TimeProvider.System), profiles);
    }

    private static HttpRequestData Request(string body = Body, string? token = Token, string origin = "https://app.example",
        string? csrf = null, string requestId = RequestId, string version = "\"v1\"", string query = "")
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
        request.SetupGet(r => r.Url).Returns(new Uri("https://app.example/api/managed-profiles" + query));
        request.SetupGet(r => r.Query).Returns(System.Web.HttpUtility.ParseQueryString(query));
        request.SetupGet(r => r.Cookies).Returns(token is null ? [] : [new HttpCookie("__Host-khata-session", token)]);
        var headers = new HttpHeadersCollection();
        headers.Add("Origin", origin);
        headers.Add("X-CSRF-Token", csrf ?? AuthenticationService.CsrfToken(token ?? Token));
        headers.Add("Idempotency-Key", requestId);
        headers.Add("If-Match", version);
        request.SetupGet(r => r.Headers).Returns(headers);
        request.SetupGet(r => r.Body).Returns(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        return request.Object;
    }
}
