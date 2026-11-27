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
            LedgerWriteStatus.ReceiptLimitReached => await JsonResponse(request, HttpStatusCode.Conflict, new { error = "The profile has reached its financial receipt limit." }, cancellationToken),
            LedgerWriteStatus.LimitExceeded => await JsonResponse(request, HttpStatusCode.BadRequest, new { error = "The resulting balance exceeds the supported limit." }, cancellationToken),
            _ => throw new InvalidOperationException("The ledger adjustment result is invalid.")
        };
    }

    [Function("ListBalanceInvestments")]
    public async Task<HttpResponseData> ListInvestments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "balances/{balanceId}/investments")] HttpRequestData request,
        string balanceId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!IsId(balanceId)) return Response(request, HttpStatusCode.NotFound);
        var page = await balances.ListInvestmentsAsync(session.UserId, balanceId,
            request.Query.GetValues("continuationToken")?.FirstOrDefault(), cancellationToken);
        return page is null ? Response(request, HttpStatusCode.NotFound)
            : await JsonResponse(request, HttpStatusCode.OK,
                new { items = page.Items.Select(FormatInvestment).ToArray(), continuationToken = page.ContinuationToken }, cancellationToken);
    }

    [Function("GetBalanceInvestment")]
    public async Task<HttpResponseData> GetInvestment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "balances/{balanceId}/investments/{investmentId}")] HttpRequestData request,
        string balanceId, string investmentId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        var investment = await balances.GetInvestmentAsync(session.UserId, balanceId, investmentId, cancellationToken);
        return investment is null ? Response(request, HttpStatusCode.NotFound)
            : await JsonResponse(request, HttpStatusCode.OK, FormatInvestment(investment), cancellationToken);
    }

    [Function("CreateBalanceInvestment")]
    public async Task<HttpResponseData> CreateInvestment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "balances/{balanceId}/investments")] HttpRequestData request,
        string balanceId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!AuthorizedWrite(request) || !FinancialWritesEnabled()) return Response(request, HttpStatusCode.Forbidden);
        var version = Version(request);
        if (version is null || !Guid.TryParseExact(AuthenticationFunctions.Header(request, "Idempotency-Key"), "D", out var requestId))
            return Response(request, HttpStatusCode.BadRequest);
        try
        {
            using var json = JsonDocument.Parse(await ReadJson(request, cancellationToken));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Response(request, HttpStatusCode.BadRequest);
            var kindText = root.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
                ? kindElement.GetString() : null;
            var kind = kindText?.ToLowerInvariant() switch
            {
                "fixed-rate" or "fixed" => InvestmentKind.FixedRate,
                "market-linked" or "market" => InvestmentKind.MarketLinked,
                _ => (InvestmentKind)99
            };
            decimal rate = 0;
            var compounding = root.TryGetProperty("compounding", out var compElement) && compElement.ValueKind == JsonValueKind.String
                ? ParseCompounding(compElement.GetString()) : CompoundingModel.Annually;
            if (root.TryGetProperty("annualRatePercent", out var rateElement))
            {
                if (!rateElement.TryGetDecimal(out rate) && !(rateElement.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(rateElement.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out rate)))
                    return Response(request, HttpStatusCode.BadRequest);
            }
            var marketReference = root.TryGetProperty("marketReference", out var refElement) && refElement.ValueKind == JsonValueKind.String
                ? refElement.GetString() : null;
            var result = await balances.CreateInvestmentAsync(session.UserId, balanceId, requestId, kind, rate,
                compounding, marketReference, version, cancellationToken);
            if (result.Investment is not null)
            {
                var status = result.Status == InvestmentWriteStatus.Created ? HttpStatusCode.Created : HttpStatusCode.OK;
                var response = await JsonResponse(request, status, FormatInvestment(result.Investment), cancellationToken);
                response.Headers.Add("Location", $"/api/balances/{balanceId}/investments/{result.Investment.Id}");
                return response;
            }
            return result.Status switch
            {
                InvestmentWriteStatus.Missing => Response(request, HttpStatusCode.NotFound),
                InvestmentWriteStatus.Conflict => Response(request, HttpStatusCode.Conflict),
                InvestmentWriteStatus.ReceiptLimitReached => await JsonResponse(request, HttpStatusCode.Conflict, new { error = "The profile has reached its financial receipt limit." }, cancellationToken),
                InvestmentWriteStatus.ValidationFailed or InvestmentWriteStatus.MarketLinkedUnavailable =>
                    await JsonResponse(request, HttpStatusCode.BadRequest,
                        new { error = "Investment details are invalid or unavailable." }, cancellationToken),
                _ => Response(request, HttpStatusCode.BadRequest)
            };
        }
        catch (JsonException)
        {
            return Response(request, HttpStatusCode.BadRequest);
        }
    }

    [Function("ListBalanceGigs")]
    public async Task<HttpResponseData> ListGigs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "balances/{balanceId}/gigs")] HttpRequestData request,
        string balanceId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        var page = await balances.ListGigsAsync(session.UserId, balanceId,
            request.Query.GetValues("continuationToken")?.FirstOrDefault(), cancellationToken);
        return page is null ? Response(request, HttpStatusCode.NotFound)
            : await JsonResponse(request, HttpStatusCode.OK,
                new { items = page.Items.Select(FormatGig).ToArray(), continuationToken = page.ContinuationToken }, cancellationToken);
    }

    [Function("GetBalanceGig")]
    public async Task<HttpResponseData> GetGig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "balances/{balanceId}/gigs/{gigId}")] HttpRequestData request,
        string balanceId, string gigId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        var gig = await balances.GetGigAsync(session.UserId, balanceId, gigId, cancellationToken);
        return gig is null ? Response(request, HttpStatusCode.NotFound)
            : await JsonResponse(request, HttpStatusCode.OK, FormatGig(gig), cancellationToken);
    }

    [Function("CreateBalanceGig")]
    public async Task<HttpResponseData> CreateGig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "balances/{balanceId}/gigs")] HttpRequestData request,
        string balanceId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!AuthorizedWrite(request) || !FinancialWritesEnabled()) return Response(request, HttpStatusCode.Forbidden);
        var version = Version(request);
        if (version is null || !Guid.TryParseExact(AuthenticationFunctions.Header(request, "Idempotency-Key"), "D", out var requestId))
            return Response(request, HttpStatusCode.BadRequest);
        var parsed = await ParseGig(request, cancellationToken);
        if (parsed.Error is not null) return parsed.Error;
        var (name, description, startDate, payout, state) = parsed.Value!.Value;
        var result = await balances.CreateGigAsync(session.UserId, balanceId, requestId, name, description,
            startDate, payout, state, version, cancellationToken);
        return await GigWriteResponse(request, balanceId, result, HttpStatusCode.Created, cancellationToken);
    }

    [Function("UpdateBalanceGig")]
    public async Task<HttpResponseData> UpdateGig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "balances/{balanceId}/gigs/{gigId}")] HttpRequestData request,
        string balanceId, string gigId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!AuthorizedWrite(request) || !FinancialWritesEnabled()) return Response(request, HttpStatusCode.Forbidden);
        var version = Version(request);
        if (version is null || !Guid.TryParseExact(AuthenticationFunctions.Header(request, "Idempotency-Key"), "D", out var requestId))
            return Response(request, HttpStatusCode.BadRequest);
        var parsed = await ParseGig(request, cancellationToken);
        if (parsed.Error is not null) return parsed.Error;
        var (name, description, startDate, payout, state) = parsed.Value!.Value;
        var result = await balances.UpdateGigAsync(session.UserId, balanceId, gigId, requestId, name, description,
            startDate, payout, state, version, cancellationToken);
        return await GigWriteResponse(request, balanceId, result, HttpStatusCode.OK, cancellationToken);
    }

    [Function("BalanceTransition")]
    public async Task<HttpResponseData> Transition(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "balances/{balanceId}/transition")] HttpRequestData request,
        string balanceId, CancellationToken cancellationToken)
    {
        var session = await Session(request, cancellationToken);
        if (session is null) return Response(request, HttpStatusCode.Unauthorized);
        if (!AuthorizedWrite(request) || !FinancialWritesEnabled()) return Response(request, HttpStatusCode.Forbidden);
        var balanceVersion = Version(request);
        if (balanceVersion is null || !Guid.TryParseExact(AuthenticationFunctions.Header(request, "Idempotency-Key"), "D", out var requestId))
            return Response(request, HttpStatusCode.BadRequest);
        try
        {
            using var json = JsonDocument.Parse(await ReadJson(request, cancellationToken));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 5)
                return Response(request, HttpStatusCode.BadRequest);
            if (!root.TryGetProperty("source", out var sourceElement) || sourceElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("destination", out var destinationElement) || destinationElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("amount", out var amountElement) ||
                !root.TryGetProperty("sourceVersion", out var sourceVersionElement) || sourceVersionElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("destinationVersion", out var destinationVersionElement) || destinationVersionElement.ValueKind != JsonValueKind.String)
                return Response(request, HttpStatusCode.BadRequest);
            decimal amount;
            if (!TryAmount(amountElement, out amount) || !FinancialAmounts.TryCents(amount, out _))
                return Response(request, HttpStatusCode.BadRequest);
            var result = await balances.TransitionAsync(session.UserId, balanceId, requestId,
                sourceElement.GetString()!, destinationElement.GetString()!, amount,
                sourceVersionElement.GetString()!, destinationVersionElement.GetString()!,
                balanceVersion, cancellationToken);
            if (result.Status is TransitionStatus.Updated or TransitionStatus.Replayed)
                return await JsonResponse(request, HttpStatusCode.OK,
                    new { balanceId, replayed = result.Status == TransitionStatus.Replayed, cash = Amount(result.Cash), balanceVersion = result.BalanceVersion,
                        source = result.Source is null ? null : FormatInvestment(result.Source),
                        destination = result.Destination is null ? null : FormatInvestment(result.Destination),
                        ledgerEntry = result.Entry is null ? null : FormatEntry(result.Entry) }, cancellationToken);
            return result.Status switch
            {
                TransitionStatus.Missing => Response(request, HttpStatusCode.NotFound),
                TransitionStatus.Conflict => Response(request, HttpStatusCode.Conflict),
                TransitionStatus.ReceiptLimitReached => await JsonResponse(request, HttpStatusCode.Conflict, new { error = "The profile has reached its financial receipt limit." }, cancellationToken),
                TransitionStatus.InsufficientFunds => await JsonResponse(request, HttpStatusCode.Conflict,
                    new { error = "Insufficient available cash." }, cancellationToken),
                TransitionStatus.ValueUnavailable => await JsonResponse(request, HttpStatusCode.Conflict,
                    new { error = "Investment valuation is unavailable." }, cancellationToken),
                _ => await JsonResponse(request, HttpStatusCode.BadRequest,
                    new { error = "The transition is invalid." }, cancellationToken)
            };
        }
        catch (JsonException)
        {
            return Response(request, HttpStatusCode.BadRequest);
        }
    }

    private async Task<( (string Name, string? Description, DateOnly StartDate, decimal Payout, GigState State)? Value,
        HttpResponseData? Error)> ParseGig(HttpRequestData request, CancellationToken cancellationToken)
    {
        try
        {
            using var json = JsonDocument.Parse(await ReadJson(request, cancellationToken));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() is < 3 or > 5 ||
                !root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("startDate", out var dateElement) || dateElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("payout", out var payoutElement) ||
                !root.TryGetProperty("state", out var stateElement) || stateElement.ValueKind != JsonValueKind.String)
                return (null, Response(request, HttpStatusCode.BadRequest));
            var name = nameElement.GetString();
            var description = root.TryGetProperty("description", out var descriptionElement) &&
                descriptionElement.ValueKind != JsonValueKind.Null
                ? descriptionElement.GetString() : null;
            if (!DateOnly.TryParseExact(dateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var startDate) ||
                !TryAmount(payoutElement, out var payout) ||
                !Enum.TryParse<GigState>(stateElement.GetString(), true, out var state))
                return (null, Response(request, HttpStatusCode.BadRequest));
            return ((name!, description, startDate, payout, state), null);
        }
        catch (JsonException)
        {
            return (null, Response(request, HttpStatusCode.BadRequest));
        }
    }

    private async Task<HttpResponseData> GigWriteResponse(HttpRequestData request, string balanceId,
        GigWriteResult result, HttpStatusCode createStatus, CancellationToken cancellationToken)
    {
        if (result.Gig is not null)
        {
            var status = result.Status is GigWriteStatus.Created ? createStatus : HttpStatusCode.OK;
            var response = await JsonResponse(request, status, FormatGig(result.Gig), cancellationToken);
            response.Headers.Add("Location", $"/api/balances/{balanceId}/gigs/{result.Gig.Id}");
            return response;
        }
        return result.Status switch
        {
            GigWriteStatus.Missing => Response(request, HttpStatusCode.NotFound),
            GigWriteStatus.Conflict => Response(request, HttpStatusCode.Conflict),
            GigWriteStatus.ReceiptLimitReached => await JsonResponse(request, HttpStatusCode.Conflict, new { error = "The profile has reached its financial receipt limit." }, cancellationToken),
            GigWriteStatus.ValidationFailed => await JsonResponse(request, HttpStatusCode.BadRequest,
                new { error = "Gig details are invalid." }, cancellationToken),
            _ => Response(request, HttpStatusCode.BadRequest)
        };
    }

    private static async Task<string> ReadJson(HttpRequestData request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var buffer = new char[4097];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (count == buffer.Length)
            throw new JsonException("Request body is too large.");
        return new string(buffer, 0, count);
    }

    private static bool TryAmount(JsonElement value, out decimal amount)
    {
        amount = 0m;
        return value.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out amount),
            JsonValueKind.Number => value.TryGetDecimal(out amount),
            _ => false
        };
    }

    private static CompoundingModel ParseCompounding(string? value) => value?.ToLowerInvariant() switch
    {
        "daily" => CompoundingModel.Daily,
        "monthly" => CompoundingModel.Monthly,
        "quarterly" => CompoundingModel.Quarterly,
        "annual" or "annually" => CompoundingModel.Annually,
        "simple" => CompoundingModel.Simple,
        _ => (CompoundingModel)99
    };

    private static object FormatInvestment(Investment investment) => new
    {
        id = investment.Id, balanceId = investment.BalanceId,
        kind = investment.Kind == InvestmentKind.FixedRate ? "fixed-rate" : "market-linked",
        state = investment.State.ToString().ToLowerInvariant(),
        annualRatePercent = investment.AnnualRatePercent.ToString("0.00", CultureInfo.InvariantCulture),
        compounding = investment.Compounding.ToString().ToLowerInvariant(),
        principal = Amount(investment.Principal),
        fundedOn = investment.FundedOn?.ToString("yyyy-MM-dd"),
        closedOn = investment.ClosedOn?.ToString("yyyy-MM-dd"),
        closingValue = investment.ClosingValue.HasValue ? Amount(investment.ClosingValue.Value) : null,
        version = investment.Version
    };

    private static object FormatGig(Gig gig) => new
    {
        id = gig.Id, balanceId = gig.BalanceId, name = gig.Name, description = gig.Description,
        startDate = gig.StartDate.ToString("yyyy-MM-dd"), payout = Amount(gig.Payout),
        state = gig.State.ToString().ToLowerInvariant(),
        completionAmount = gig.CompletionAmount.HasValue ? Amount(gig.CompletionAmount.Value) : null,
        completedAt = gig.CompletedAt, version = gig.Version
    };

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
