using System.Net;
using System.Text.Json;
using Khata.Application;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Khata.Api;

public sealed class AuthenticationFunctions(AuthenticationService authentication, AuthSettings settings, ILogger<AuthenticationFunctions> logger)
{
    private const string SessionCookie = "__Host-khata-session";
    private const string FlowCookie = "__Host-khata-flow";

    [Function("GoogleLoginStart")]
    public async Task<HttpResponseData> LoginStart(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/google/start")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        if (Header(request, "Origin") != settings.Origin)
            return request.CreateResponse(HttpStatusCode.Forbidden);
        var start = await authentication.StartAsync(cancellationToken);
        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Cache-Control", "no-store");
        SetCookie(response, FlowCookie, start.BrowserBinding, 600);
        await response.WriteAsJsonAsync(new { clientId = settings.GoogleClientId, state = start.State, nonce = start.Nonce }, cancellationToken);
        return response;
    }

    [Function("GoogleLoginComplete")]
    public async Task<HttpResponseData> LoginComplete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/google/complete")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        if (Header(request, "Origin") != settings.Origin)
            return request.CreateResponse(HttpStatusCode.Forbidden);

        var binding = Cookie(request, FlowCookie);
        LoginRequest? payload;
        try
        {
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var buffer = new char[20001];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (count == buffer.Length)
                return request.CreateResponse(HttpStatusCode.BadRequest);
            payload = JsonSerializer.Deserialize<LoginRequest>(buffer.AsSpan(0, count),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return request.CreateResponse(HttpStatusCode.BadRequest);
        }

        LoginResult? result = null;
        var status = HttpStatusCode.Unauthorized;
        try
        {
            if (payload?.State is not null && payload.Credential is not null && binding is not null)
                result = await authentication.CompleteAsync(payload.State, binding, payload.Credential, Cookie(request, SessionCookie), cancellationToken);
        }
        catch (SecurityTokenException)
        {
            logger.LogWarning("Google sign-in failed because the identity token was invalid.");
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Google sign-in failed because identity metadata could not be retrieved.");
            status = HttpStatusCode.ServiceUnavailable;
        }
        var response = request.CreateResponse(result is null ? status : HttpStatusCode.NoContent);
        response.Headers.Add("Cache-Control", "no-store");
        SetCookie(response, FlowCookie, "", 0);
        if (result is not null)
            SetCookie(response, SessionCookie, result.SessionToken, 28800);
        return response;
    }

    [Function("CurrentSession")]
    public async Task<HttpResponseData> CurrentSession(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/session")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var token = Cookie(request, SessionCookie);
        var session = await authentication.GetSessionAsync(token, cancellationToken);
        var response = request.CreateResponse(session is null ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        response.Headers.Add("Cache-Control", "no-store");
        if (session is not null)
            await response.WriteAsJsonAsync(new { userId = session.UserId, csrfToken = AuthenticationService.CsrfToken(token!) }, cancellationToken);
        return response;
    }

    [Function("Logout")]
    public async Task<HttpResponseData> Logout(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/logout")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var origin = Header(request, "Origin");
        var csrf = Header(request, "X-CSRF-Token");
        if (origin != settings.Origin || !await authentication.LogoutAsync(Cookie(request, SessionCookie), csrf, cancellationToken))
            return request.CreateResponse(HttpStatusCode.Forbidden);
        var response = request.CreateResponse(HttpStatusCode.NoContent);
        response.Headers.Add("Cache-Control", "no-store");
        SetCookie(response, SessionCookie, "", 0);
        return response;
    }

    private static string? Cookie(HttpRequestData request, string name)
    {
        var values = request.Cookies.Where(cookie => cookie.Name == name).Select(cookie => cookie.Value).Take(2).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    private static string? Header(HttpRequestData request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;

    private static void SetCookie(HttpResponseData response, string name, string value, int maxAge)
    {
        response.Cookies.Append(new HttpCookie(name, value)
        {
            Path = "/", Secure = true, HttpOnly = true, SameSite = SameSite.Lax, MaxAge = maxAge
        });
    }

    private sealed record LoginRequest(string? State, string? Credential);
}
