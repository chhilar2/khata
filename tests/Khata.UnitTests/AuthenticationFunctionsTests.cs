using System.Net;
using System.Text.Json;
using Azure.Core.Serialization;
using Khata.Api;
using Khata.Application;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Khata.UnitTests;

public class AuthenticationFunctionsTests
{
    private static readonly AuthSettings Settings = new("https://app.example", "client", "AzureCli", null,
        "https://cosmos.example", "database", "container");

    [Fact]
    public async Task MissingSessionReturnsUnauthorized()
    {
        var (request, _) = CreateRequest();
        var functions = CreateFunctions(new Mock<IAuthenticationStore>().Object);

        var response = await functions.CurrentSession(request.Object, default);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control"));
    }

    [Fact]
    public async Task LoginEndpointsRejectCrossOriginRequests()
    {
        var store = new Mock<IAuthenticationStore>();
        var functions = CreateFunctions(store.Object);
        var (start, _) = CreateRequest();
        start.Object.Headers.Add("Origin", "https://attacker.example");
        var (complete, _) = CreateRequest();
        complete.Object.Headers.Add("Origin", "https://attacker.example");

        Assert.Equal(HttpStatusCode.Forbidden, (await functions.LoginStart(start.Object, default)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await functions.LoginComplete(complete.Object, default)).StatusCode);
        store.Verify(s => s.SaveFlowAsync(It.IsAny<LoginFlow>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.ConsumeFlowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoginStartReturnsBrowserFieldsAndSetsBindingCookie()
    {
        var store = new Mock<IAuthenticationStore>();
        LoginFlow? savedFlow = null;
        store.Setup(s => s.SaveFlowAsync(It.IsAny<LoginFlow>(), It.IsAny<CancellationToken>()))
            .Callback<LoginFlow, CancellationToken>((flow, _) => savedFlow = flow)
            .Returns(Task.CompletedTask);
        var (request, cookies) = CreateRequest();
        request.Object.Headers.Add("Origin", Settings.Origin);

        var response = await CreateFunctions(store.Object).LoginStart(request.Object, default);
        response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Body);
        var state = json.RootElement.GetProperty("state").GetString();
        var nonce = json.RootElement.GetProperty("nonce").GetString();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Settings.GoogleClientId, json.RootElement.GetProperty("clientId").GetString());
        Assert.NotNull(state);
        Assert.NotNull(nonce);
        Assert.NotNull(savedFlow);
        Assert.Equal(AuthenticationService.Hash(state), savedFlow.Id);
        Assert.Equal(AuthenticationService.Hash(nonce), savedFlow.NonceHash);
        cookies.Verify(c => c.Append(It.Is<IHttpCookie>(cookie =>
            cookie.Name == "__Host-khata-flow" && cookie.MaxAge == 600 &&
            cookie.Secure == true && cookie.HttpOnly == true && cookie.SameSite == SameSite.Lax &&
            cookie.Path == "/" && AuthenticationService.Hash(cookie.Value) == savedFlow.BindingHash)), Times.Once);
    }

    [Fact]
    public async Task LoginRejectsMalformedJson()
    {
        var (request, _) = CreateRequest();
        request.Object.Headers.Add("Origin", Settings.Origin);
        request.SetupGet(r => r.Body).Returns(new MemoryStream("invalid"u8.ToArray()));

        var response = await CreateFunctions(Mock.Of<IAuthenticationStore>()).LoginComplete(request.Object, default);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LogoutRequiresOriginAndValidCsrf()
    {
        const string token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var session = new UserSession(AuthenticationService.Hash(token), "user", DateTimeOffset.UtcNow.AddMinutes(5));
        var store = new Mock<IAuthenticationStore>();
        store.Setup(s => s.GetSessionAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var functions = CreateFunctions(store.Object);
        var (request, _) = CreateRequest(new HttpCookie("__Host-khata-session", token));
        request.Object.Headers.Add("Origin", "https://attacker.example");
        request.Object.Headers.Add("X-CSRF-Token", AuthenticationService.CsrfToken(token));
        Assert.Equal(HttpStatusCode.Forbidden, (await functions.Logout(request.Object, default)).StatusCode);
        store.Verify(s => s.DeleteSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var (wrongCsrf, _) = CreateRequest(new HttpCookie("__Host-khata-session", token));
        wrongCsrf.Object.Headers.Add("Origin", Settings.Origin);
        wrongCsrf.Object.Headers.Add("X-CSRF-Token", "wrong");
        Assert.Equal(HttpStatusCode.Forbidden, (await functions.Logout(wrongCsrf.Object, default)).StatusCode);
        store.Verify(s => s.DeleteSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var (valid, cookies) = CreateRequest(new HttpCookie("__Host-khata-session", token));
        valid.Object.Headers.Add("Origin", Settings.Origin);
        valid.Object.Headers.Add("X-CSRF-Token", AuthenticationService.CsrfToken(token));
        Assert.Equal(HttpStatusCode.NoContent, (await functions.Logout(valid.Object, default)).StatusCode);
        store.Verify(s => s.DeleteSessionAsync(session.Id, It.IsAny<CancellationToken>()), Times.Once);
        cookies.Verify(c => c.Append(It.Is<IHttpCookie>(cookie =>
            cookie.Name == "__Host-khata-session" && cookie.MaxAge == 0 &&
            cookie.Secure == true && cookie.HttpOnly == true && cookie.SameSite == SameSite.Lax && cookie.Path == "/")), Times.Once);
    }

    private static AuthenticationFunctions CreateFunctions(IAuthenticationStore store) =>
        new(new AuthenticationService(store, new Mock<IIdentityProvider>().Object, TimeProvider.System),
            Settings, Mock.Of<ILogger<AuthenticationFunctions>>());

    private static (Mock<HttpRequestData>, Mock<HttpCookies>) CreateRequest(params IHttpCookie[] requestCookies)
    {
        var services = new ServiceCollection()
            .Configure<WorkerOptions>(options => options.Serializer = new JsonObjectSerializer())
            .BuildServiceProvider();
        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.InstanceServices).Returns(services);
        var response = new Mock<HttpResponseData>(context.Object);
        var cookies = new Mock<HttpCookies>();
        response.SetupProperty(r => r.StatusCode);
        response.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection());
        response.SetupGet(r => r.Cookies).Returns(cookies.Object);
        response.SetupGet(r => r.Body).Returns(new MemoryStream());
        var request = new Mock<HttpRequestData>(context.Object);
        request.Setup(r => r.CreateResponse()).Returns(response.Object);
        request.SetupGet(r => r.Cookies).Returns(requestCookies);
        request.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection());
        return (request, cookies);
    }
}
