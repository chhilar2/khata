using System.Net;
using System.Text.Json;
using Khata.Application;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Khata.Api;

public sealed class AccountFunctions(AuthenticationService authentication, IAccountStore accounts, AuthSettings settings)
{
    [Function("GetAccount")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "account")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var session = await authentication.GetSessionAsync(
            AuthenticationFunctions.Cookie(request, AuthenticationFunctions.SessionCookie), cancellationToken);
        if (session is null)
            return Response(request, HttpStatusCode.Unauthorized);
        var account = await accounts.GetAsync(session.UserId, cancellationToken);
        if (account is null)
            return Response(request, HttpStatusCode.NotFound);
        var result = Response(request, HttpStatusCode.OK);
        var profile = account.Profile;
        await result.WriteAsJsonAsync(new
        {
            provider = profile.Provider, email = profile.Email,
            displayName = profile.Fields.DisplayName, contactEmail = profile.Fields.ContactEmail,
            phone = profile.Fields.Phone, country = profile.Fields.Country, currency = profile.Fields.Currency,
            version = account.Version
        }, cancellationToken);
        return result;
    }

    [Function("UpdateAccount")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "account")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var token = AuthenticationFunctions.Cookie(request, AuthenticationFunctions.SessionCookie);
        var session = await authentication.GetSessionAsync(token, cancellationToken);
        if (session is null)
            return Response(request, HttpStatusCode.Unauthorized);
        if (AuthenticationFunctions.Header(request, "Origin") != settings.Origin ||
            !AuthenticationService.FixedEquals(AuthenticationService.CsrfToken(token!),
                AuthenticationFunctions.Header(request, "X-CSRF-Token")))
            return Response(request, HttpStatusCode.Forbidden);
        var version = AuthenticationFunctions.Header(request, "If-Match");
        if (string.IsNullOrWhiteSpace(version) || version.Length > 256 || version == "*" ||
            version.Contains(',') || version.Any(char.IsControl))
            return Response(request, HttpStatusCode.BadRequest);

        AccountFields? fields;
        try
        {
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var buffer = new char[8193];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (count == buffer.Length)
                return Response(request, HttpStatusCode.BadRequest);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return Response(request, HttpStatusCode.BadRequest);
            var properties = json.RootElement.EnumerateObject().ToArray();
            var names = new[] { "displayName", "contactEmail", "phone", "country", "currency" };
            if (properties.Length != names.Length ||
                names.Any(name => properties.Count(property => property.NameEquals(name)) != 1) ||
                properties.Any(property => property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                return Response(request, HttpStatusCode.BadRequest);
            fields = JsonSerializer.Deserialize<AccountFields>(json.RootElement,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return Response(request, HttpStatusCode.BadRequest);
        }
        if (fields is null)
            return Response(request, HttpStatusCode.BadRequest);
        var (normalized, errors) = AccountValidation.Validate(fields);
        if (errors.Count != 0)
        {
            var invalid = Response(request, HttpStatusCode.BadRequest);
            await invalid.WriteAsJsonAsync(new { errors }, cancellationToken);
            return invalid;
        }
        var outcome = await accounts.UpdateAsync(session.UserId, normalized, version, cancellationToken);
        return Response(request, outcome switch
        {
            AccountUpdateResult.Updated => HttpStatusCode.NoContent,
            AccountUpdateResult.Conflict => HttpStatusCode.Conflict,
            AccountUpdateResult.Missing => HttpStatusCode.NotFound,
            _ => throw new InvalidOperationException("Unknown account update result.")
        });
    }

    private static HttpResponseData Response(HttpRequestData request, HttpStatusCode status)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }
}
