using System.IdentityModel.Tokens.Jwt;
using Khata.Application;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Khata.Infrastructure;

public sealed class GoogleIdentityProvider(string clientId, HttpClient httpClient) : IIdentityProvider
{
    private const string Issuer = "https://accounts.google.com";
    private readonly ConfigurationManager<OpenIdConnectConfiguration> configuration = new(
        "https://accounts.google.com/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever(httpClient) { RequireHttps = true });

    public async Task<ValidatedIdentity> ValidateTokenAsync(string token, string nonceHash, CancellationToken cancellationToken)
    {
        var metadata = await configuration.GetConfigurationAsync(cancellationToken);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuers = [Issuer, "accounts.google.com"],
            ValidateAudience = true, ValidAudience = clientId, IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidateLifetime = true, RequireExpirationTime = true,
            ValidateIssuerSigningKey = true, IssuerSigningKeys = metadata.SigningKeys,
            RequireSignedTokens = true, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        try
        {
            var principal = handler.ValidateToken(token, parameters, out var validated);
            var jwt = (JwtSecurityToken)validated;
            var subject = principal.FindFirst("sub")?.Value;
            var tokenNonce = principal.FindFirst("nonce")?.Value;
            var authorizedParty = principal.FindFirst("azp")?.Value;
            if (string.IsNullOrWhiteSpace(subject) || tokenNonce is null ||
                !AuthenticationService.FixedEquals(nonceHash, AuthenticationService.Hash(tokenNonce)) ||
                (authorizedParty is not null && authorizedParty != clientId) ||
                (jwt.Audiences.Count() > 1 && authorizedParty != clientId))
                throw new SecurityTokenException("The identity token claims are invalid.");
            var name = principal.FindFirst("name")?.Value;
            var email = principal.FindFirst("email_verified")?.Value == "true"
                ? principal.FindFirst("email")?.Value : null;
            var (profile, errors) = AccountValidation.Validate(new AccountFields(name, email, null, null, null));
            if (errors.Count != 0)
                throw new SecurityTokenException("The identity token profile claims are invalid.");
            return new ValidatedIdentity(new ExternalIdentity("google", Issuer, subject), profile.DisplayName, profile.ContactEmail);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            configuration.RequestRefresh();
            throw;
        }
    }
}
