using System.Net;
using System.Globalization;
using System.Text.Json;
using Khata.Application;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

namespace Khata.Api;

public sealed class BalanceFunctions(AuthenticationService authentication, IBalanceStore balances, AuthSettings settings, IConfiguration configuration)
{
    [Function("GetProfileBalance")]
    public async Task<HttpResponseData> GetProfileBalance(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "managed-profiles/{profileId}/balance")] HttpRequestData request,
        string profileId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!IsId(profileId)) return Response(request, HttpStatusCode.NotFound);
        var balance = await balances.GetOrCreateForProfileAsync(session.UserId, profileId, cancellationToken);
        return balance is null ? Response(request, HttpStatusCode.NotFound)
            : await JsonResponse(request, HttpStatusCode.OK, Format(balance), cancellationToken);
    }

    [Function("GetBalance")]
    public async Task<HttpResponseData> GetBalance(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "balances/{balanceId}")] HttpRequestData request,
        string balanceId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!IsId(balanceId)) return Response(request, HttpStatusCode.NotFound);
        var balance = await balances.GetAsync(session.UserId, balanceId, cancellationToken);
        return balance is null ? Response(request, HttpStatusCode.NotFound)
            : await JsonResponse(request, HttpStatusCode.OK, Format(balance), cancellationToken);
    }

    [Function("GetBalanceCash")]
    public async Task<HttpResponseData> GetCash(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "balances/{balanceId}/cash")] HttpRequestData request,
        string balanceId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!IsId(balanceId)) return Response(request, HttpStatusCode.NotFound);
        var balance = await balances.GetAsync(session.UserId, balanceId, cancellationToken);
        return balance is null ? Response(request, HttpStatusCode.NotFound)
            : await JsonResponse(request, HttpStatusCode.OK,
                new { balanceId = balance.Id, currency = balance.Currency, amount = Amount(balance.Cash), version = balance.Version }, cancellationToken);
    }

    [Function("SetBalanceCurrency")]
    public async Task<HttpResponseData> SetCurrency(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "balances/{balanceId}")] HttpRequestData request,
        string balanceId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!AuthorizedWrite(request)) return Response(request, HttpStatusCode.Forbidden);
        if (!FinancialWritesEnabled())
            return await JsonResponse(request, HttpStatusCode.ServiceUnavailable,
                new { error = "Financial actions are not enabled." }, cancellationToken);
        if (!IsId(balanceId)) return Response(request, HttpStatusCode.NotFound);
        var version = Version(request);
        if (version is null) return Response(request, HttpStatusCode.BadRequest);

        try
        {
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var buffer = new char[1025];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (count == buffer.Length) return Response(request, HttpStatusCode.BadRequest);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return Response(request, HttpStatusCode.BadRequest);
            var properties = json.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || !properties[0].NameEquals("currency") ||
                properties[0].Value.ValueKind != JsonValueKind.String || properties[0].Value.GetString() != "USD")
                return await JsonResponse(request, HttpStatusCode.BadRequest,
                    new { errors = new { currency = "Only USD is supported." } }, cancellationToken);
        }
        catch (JsonException)
        {
            return Response(request, HttpStatusCode.BadRequest);
        }

        var result = await balances.SetCurrencyAsync(session.UserId, balanceId, "USD", version, cancellationToken);
        return result.Status switch
        {
            BalanceUpdateStatus.Updated when result.Balance is not null =>
                await JsonResponse(request, HttpStatusCode.OK, Format(result.Balance), cancellationToken),
            BalanceUpdateStatus.Missing => Response(request, HttpStatusCode.NotFound),
            BalanceUpdateStatus.Conflict => Response(request, HttpStatusCode.Conflict),
            _ => throw new InvalidOperationException("The balance update result is invalid.")
        };
    }

    [Function("ListBalanceLedger")]
    public async Task<HttpResponseData> ListLedger(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "balances/{balanceId}/ledger")] HttpRequestData request,
        string balanceId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!IsId(balanceId)) return Response(request, HttpStatusCode.NotFound);
        var values = request.Query.GetValues("continuationToken");
        if (values is { Length: > 1 } || values?.FirstOrDefault() is { Length: > 8192 })
            return Response(request, HttpStatusCode.BadRequest);
        try
        {
            var page = await balances.ListLedgerAsync(session.UserId, balanceId, values?.FirstOrDefault(), cancellationToken);
            return page is null ? Response(request, HttpStatusCode.NotFound)
                : await JsonResponse(request, HttpStatusCode.OK,
                    new { items = page.Items.Select(FormatEntry).ToArray(), continuationToken = page.ContinuationToken }, cancellationToken);
        }
        catch (InvalidBalanceContinuationException)
        {
            return Response(request, HttpStatusCode.BadRequest);
        }
    }

    [Function("GetBalanceLedgerEntry")]
    public async Task<HttpResponseData> GetLedgerEntry(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "balances/{balanceId}/ledger/{entryId}")] HttpRequestData request,
        string balanceId, string entryId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!IsId(balanceId) || !IsId(entryId)) return Response(request, HttpStatusCode.NotFound);
        var entry = await balances.GetLedgerEntryAsync(session.UserId, balanceId, entryId, cancellationToken);
        return entry is null ? Response(request, HttpStatusCode.NotFound)
            : await JsonResponse(request, HttpStatusCode.OK, FormatEntry(entry), cancellationToken);
    }

    [Function("AdjustBalanceCash")]
    public async Task<HttpResponseData> AdjustCash(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "balances/{balanceId}/ledger")] HttpRequestData request,
        string balanceId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!AuthorizedWrite(request)) return Response(request, HttpStatusCode.Forbidden);
        if (!FinancialWritesEnabled())
            return await JsonResponse(request, HttpStatusCode.ServiceUnavailable,
                new { error = "Financial actions are not enabled." }, cancellationToken);
        if (!IsId(balanceId)) return Response(request, HttpStatusCode.NotFound);
        var version = Version(request);
        if (version is null || !Guid.TryParseExact(AuthenticationFunctions.Header(request, "Idempotency-Key"), "D", out var requestId))
            return Response(request, HttpStatusCode.BadRequest);
        string? operation;
        decimal amount;
        try
        {
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var buffer = new char[1025];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (count == buffer.Length) return Response(request, HttpStatusCode.BadRequest);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return Response(request, HttpStatusCode.BadRequest);
            var properties = json.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 2 || properties.Count(p => p.NameEquals("operation")) != 1 ||
                properties.Count(p => p.NameEquals("amount")) != 1)
                return Response(request, HttpStatusCode.BadRequest);
            var operationValue = json.RootElement.GetProperty("operation");
            var amountValue = json.RootElement.GetProperty("amount");
            operation = operationValue.ValueKind == JsonValueKind.String ? operationValue.GetString() : null;
            amount = 0;
            var validAmount = amountValue.ValueKind switch
            {
                JsonValueKind.String => decimal.TryParse(amountValue.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount),
                JsonValueKind.Number => amountValue.TryGetDecimal(out amount),
                _ => false
            };
            if (operation is not ("deposit" or "withdrawal") || !validAmount || !FinancialAmounts.TryCents(amount, out _))
                return Response(request, HttpStatusCode.BadRequest);
        }
        catch (JsonException)
        {
            return Response(request, HttpStatusCode.BadRequest);
        }
        var result = await balances.AdjustCashAsync(session.UserId, balanceId, requestId, operation, amount, version, cancellationToken);
        if (result.Status is LedgerWriteStatus.Created or LedgerWriteStatus.Replayed && result.Entry is not null)
        {
            var response = await JsonResponse(request, result.Status == LedgerWriteStatus.Created ? HttpStatusCode.Created : HttpStatusCode.OK,
                FormatEntry(result.Entry), cancellationToken);
            response.Headers.Add("Location", $"/api/balances/{balanceId}/ledger/{result.Entry.Id}");
            return response;
        }
        return result.Status switch
        {
            LedgerWriteStatus.Missing => Response(request, HttpStatusCode.NotFound),
            LedgerWriteStatus.Conflict => Response(request, HttpStatusCode.Conflict),
            LedgerWriteStatus.InsufficientFunds => await JsonResponse(request, HttpStatusCode.Conflict, new { error = "Insufficient available cash." }, cancellationToken),
            LedgerWriteStatus.LimitExceeded => await JsonResponse(request, HttpStatusCode.BadRequest, new { error = "The resulting balance exceeds the supported limit." }, cancellationToken),
            _ => throw new InvalidOperationException("The ledger adjustment result is invalid.")
        };
    }

    private Task<UserSession?> Session(HttpRequestData request, CancellationToken cancellationToken) =>
        authentication.GetSessionAsync(AuthenticationFunctions.Cookie(request, AuthenticationFunctions.SessionCookie), cancellationToken);

    private bool FinancialWritesEnabled()
    {
        var value = configuration["FinancialFeatures:Enabled"];
        return value is null ? false : bool.TryParse(value, out var enabled)
            ? enabled : throw new InvalidOperationException("FinancialFeatures:Enabled must be true or false.");
    }

    private bool AuthorizedWrite(HttpRequestData request)
    {
        var token = AuthenticationFunctions.Cookie(request, AuthenticationFunctions.SessionCookie);
        return token is not null && AuthenticationFunctions.Header(request, "Origin") == settings.Origin &&
            AuthenticationService.FixedEquals(AuthenticationService.CsrfToken(token), AuthenticationFunctions.Header(request, "X-CSRF-Token"));
    }

    private static bool IsId(string id) => Guid.TryParseExact(id, "N", out _);

    private static string? Version(HttpRequestData request)
    {
        var value = AuthenticationFunctions.Header(request, "If-Match");
        return string.IsNullOrWhiteSpace(value) || value.Length > 256 || value == "*" ||
            value.Contains(',') || value.Any(char.IsControl) ? null : value;
    }

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static object Format(Balance balance) => new
    {
        id = balance.Id, profileId = balance.ProfileId, currency = balance.Currency,
        cash = Amount(balance.Cash), netWorth = (string?)null,
        netWorthAvailable = false, valuationUnavailableReason = "Investment valuation is not available yet.",
        version = balance.Version
    };

    private static object FormatEntry(LedgerEntry entry) => new
    {
        id = entry.Id, balanceId = entry.BalanceId, operation = entry.Kind,
        amount = Amount(entry.Amount), cashAfter = Amount(entry.CashAfter), createdAt = entry.CreatedAt
    };

    private static HttpResponseData Response(HttpRequestData request, HttpStatusCode status)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    private static async Task<HttpResponseData> JsonResponse<T>(HttpRequestData request, HttpStatusCode status, T value, CancellationToken cancellationToken)
    {
        var response = Response(request, status);
        await response.WriteAsJsonAsync(value, cancellationToken);
        return response;
    }
}
