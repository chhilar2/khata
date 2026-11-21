using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Khata.Application;
using Khata.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace Khata.UnitTests;

public class GoogleIdentityProviderTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("issuer", false)]
    [InlineData("audience", false)]
    [InlineData("nonce", false)]
    [InlineData("authorized-party", false)]
    [InlineData("multiple-audiences", false)]
    [InlineData("expired", false)]
    [InlineData("signature", false)]
    public async Task ValidatesGoogleIdToken(string scenario, bool accepted)
    {
        using var key = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(scenario == "signature" ? otherKey : key) { KeyId = "test-key" };
        var claims = new List<Claim>
        {
            new("sub", "google-subject"),
            new("nonce", scenario == "nonce" ? "wrong" : "nonce")
        };
        if (scenario == "authorized-party") claims.Add(new Claim("azp", "other-client"));
        if (scenario == "multiple-audiences") claims.Add(new Claim("aud", "other-client"));
        var token = new JwtSecurityToken(
            scenario == "issuer" ? "https://wrong.example" : "https://accounts.google.com",
            scenario == "audience" ? "other-client" : "client",
            claims,
            expires: DateTime.UtcNow.AddMinutes(scenario == "expired" ? -5 : 5),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        var publicKey = key.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", kid = "test-key", use = "sig", alg = "RS256",
                n = Base64UrlEncoder.Encode(publicKey.Modulus), e = Base64UrlEncoder.Encode(publicKey.Exponent) } }
        });
        const string discovery = """
            {"issuer":"https://accounts.google.com","jwks_uri":"https://accounts.google.com/keys",
            "authorization_endpoint":"https://accounts.google.com/authorize","token_endpoint":"https://oauth2.googleapis.com/token"}
            """;
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/.well-known/openid-configuration" => discovery,
            "/keys" => jwks,
            _ => throw new InvalidOperationException("Unexpected HTTP request.")
        }));
        var provider = new GoogleIdentityProvider("client", client);

        if (accepted)
        {
            var identity = await provider.ValidateTokenAsync(jwt, AuthenticationService.Hash("nonce"), default);
            Assert.Equal("google-subject", identity.Subject);
            Assert.Equal("https://accounts.google.com", identity.Issuer);
        }
        else
        {
            await Assert.ThrowsAnyAsync<SecurityTokenException>(() => provider.ValidateTokenAsync(jwt, AuthenticationService.Hash("nonce"), default));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
    }
}
