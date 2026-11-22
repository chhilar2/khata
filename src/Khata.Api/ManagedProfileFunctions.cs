using System.Net;
using System.Text.Json;
using Khata.Application;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Khata.Api;

public sealed class ManagedProfileFunctions(
    AuthenticationService authentication, IManagedProfileStore profiles, AuthSettings settings, TimeProvider clock)
{
    [Function("ListManagedProfiles")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "managed-profiles")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null)
            return Response(request, HttpStatusCode.Unauthorized);
        var values = request.Query.GetValues("continuationToken");
        if (values is { Length: > 1 } || values?.FirstOrDefault() is { Length: > 8192 })
            return Response(request, HttpStatusCode.BadRequest);
        try
        {
            var page = await profiles.ListAsync(session.UserId, values?.FirstOrDefault(), cancellationToken);
            return await JsonResponse(request, HttpStatusCode.OK,
                new { items = page.Items.Select(Format).ToArray(), continuationToken = page.ContinuationToken }, cancellationToken);
        }
        catch (InvalidProfileContinuationException)
        {
            return Response(request, HttpStatusCode.BadRequest);
        }
    }

    [Function("GetManagedProfile")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "managed-profiles/{id}")] HttpRequestData request,
        string id, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null)
            return Response(request, HttpStatusCode.Unauthorized);
        if (!Guid.TryParseExact(id, "N", out _))
            return Response(request, HttpStatusCode.NotFound);
        var profile = await profiles.GetAsync(session.UserId, id, cancellationToken);
        return profile is null ? Response(request, HttpStatusCode.NotFound)
            : await JsonResponse(request, HttpStatusCode.OK, Format(profile), cancellationToken);
    }

    [Function("CreateManagedProfile")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "managed-profiles")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null)
            return Response(request, HttpStatusCode.Unauthorized);
        if (!AuthorizedWrite(request))
            return Response(request, HttpStatusCode.Forbidden);
        if (!Guid.TryParseExact(AuthenticationFunctions.Header(request, "Idempotency-Key"), "D", out var requestId))
            return Response(request, HttpStatusCode.BadRequest);
        var (fields, error) = await ParseFields(request, cancellationToken);
        if (error is not null)
            return error;
        var result = await profiles.CreateAsync(session.UserId, requestId, fields!, cancellationToken);
        if (result.Status is ProfileCreateStatus.Conflict or ProfileCreateStatus.Gone)
            return Response(request, result.Status == ProfileCreateStatus.Gone ? HttpStatusCode.Gone : HttpStatusCode.Conflict);
        if (result.Profile is null)
            throw new InvalidOperationException("The profile creation result is invalid.");
        var status = result.Status == ProfileCreateStatus.Created ? HttpStatusCode.Created : HttpStatusCode.OK;
        var response = await JsonResponse(request, status, Format(result.Profile), cancellationToken);
        response.Headers.Add("Location", "/api/managed-profiles/" + result.Profile.Id);
        return response;
    }

    [Function("UpdateManagedProfile")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "managed-profiles/{id}")] HttpRequestData request,
        string id, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null)
            return Response(request, HttpStatusCode.Unauthorized);
        if (!AuthorizedWrite(request))
            return Response(request, HttpStatusCode.Forbidden);
        if (!Guid.TryParseExact(id, "N", out _))
            return Response(request, HttpStatusCode.NotFound);
        var version = Version(request);
        if (version is null)
            return Response(request, HttpStatusCode.BadRequest);
        var (fields, error) = await ParseFields(request, cancellationToken);
        if (error is not null)
            return error;
        var result = await profiles.UpdateAsync(session.UserId, id, fields!, version, cancellationToken);
        return await WriteResponse(request, result, cancellationToken);
    }

    [Function("DeleteManagedProfile")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "managed-profiles/{id}")] HttpRequestData request,
        string id, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null)
            return Response(request, HttpStatusCode.Unauthorized);
        if (!AuthorizedWrite(request))
            return Response(request, HttpStatusCode.Forbidden);
        if (!Guid.TryParseExact(id, "N", out _))
            return Response(request, HttpStatusCode.NotFound);
        var version = Version(request);
        if (version is null)
            return Response(request, HttpStatusCode.BadRequest);
        var result = await profiles.DeleteAsync(session.UserId, id, version, cancellationToken);
        return await WriteResponse(request, result, cancellationToken);
    }

    private Task<UserSession?> Session(HttpRequestData request, CancellationToken cancellationToken) =>
        authentication.GetSessionAsync(AuthenticationFunctions.Cookie(request, AuthenticationFunctions.SessionCookie), cancellationToken);

    private bool AuthorizedWrite(HttpRequestData request)
    {
        var token = AuthenticationFunctions.Cookie(request, AuthenticationFunctions.SessionCookie);
        return token is not null && AuthenticationFunctions.Header(request, "Origin") == settings.Origin &&
            AuthenticationService.FixedEquals(AuthenticationService.CsrfToken(token), AuthenticationFunctions.Header(request, "X-CSRF-Token"));
    }

    private static string? Version(HttpRequestData request)
    {
        var value = AuthenticationFunctions.Header(request, "If-Match");
        return string.IsNullOrWhiteSpace(value) || value.Length > 256 || value == "*" ||
            value.Contains(',') || value.Any(char.IsControl) ? null : value;
    }

    private async Task<(ManagedProfileFields? Fields, HttpResponseData? Error)> ParseFields(
        HttpRequestData request, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var buffer = new char[2049];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (count == buffer.Length)
                return (null, Response(request, HttpStatusCode.BadRequest));
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return (null, Response(request, HttpStatusCode.BadRequest));
            var properties = json.RootElement.EnumerateObject().ToArray();
            if (properties.Count(p => p.NameEquals("displayName")) != 1 ||
                properties.Count(p => p.NameEquals("dateOfBirth")) > 1 ||
                properties.Any(p => !p.NameEquals("displayName") && !p.NameEquals("dateOfBirth")) ||
                properties.Any(p => p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                return (null, Response(request, HttpStatusCode.BadRequest));
            var input = JsonSerializer.Deserialize<ManagedProfileFields>(json.RootElement,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (input is null)
                return (null, Response(request, HttpStatusCode.BadRequest));
            var (fields, errors) = ManagedProfileValidation.Validate(input, clock);
            return errors.Count == 0 ? (fields, null)
                : (null, await JsonResponse(request, HttpStatusCode.BadRequest, new { errors }, cancellationToken));
        }
        catch (JsonException)
        {
            return (null, Response(request, HttpStatusCode.BadRequest));
        }
    }

    private static async Task<HttpResponseData> WriteResponse(
        HttpRequestData request, ProfileWriteResult result, CancellationToken cancellationToken) =>
        result.Status switch
        {
            ProfileWriteStatus.Updated when result.Profile is not null => await JsonResponse(request, HttpStatusCode.OK, Format(result.Profile), cancellationToken),
            ProfileWriteStatus.Deleted => Response(request, HttpStatusCode.NoContent),
            ProfileWriteStatus.Conflict => Response(request, HttpStatusCode.Conflict),
            ProfileWriteStatus.Missing => Response(request, HttpStatusCode.NotFound),
            _ => throw new InvalidOperationException("The profile update result is invalid.")
        };

    private static HttpResponseData Response(HttpRequestData request, HttpStatusCode status)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    private static object Format(ManagedProfile profile) => new
    {
        id = profile.Id, displayName = profile.DisplayName, dateOfBirth = profile.DateOfBirth,
        createdAt = profile.CreatedAt, updatedAt = profile.UpdatedAt, version = profile.Version
    };

    private static async Task<HttpResponseData> JsonResponse<T>(HttpRequestData request, HttpStatusCode status, T value, CancellationToken cancellationToken)
    {
        var response = Response(request, status);
        await response.WriteAsJsonAsync(value, cancellationToken);
        return response;
    }
}
