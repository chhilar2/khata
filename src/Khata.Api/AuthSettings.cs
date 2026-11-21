using Microsoft.Extensions.Configuration;

namespace Khata.Api;

public sealed record AuthSettings(string Origin, string GoogleClientId, string CredentialSource, string? ManagedIdentityClientId,
    string CosmosEndpoint, string CosmosDatabase, string CosmosContainer)
{
    public static AuthSettings Load(IConfiguration config)
    {
        string Required(string key) => !string.IsNullOrWhiteSpace(config[key])
            ? config[key]!
            : throw new InvalidOperationException($"Missing configuration: {key}.");

        var origin = Required("Auth:Origin");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.Host == "localhost")) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Auth:Origin must be an HTTPS origin (HTTP is allowed for localhost only).");
        var source = Required("Auth:CredentialSource");
        if (source is not ("AzureCli" or "ManagedIdentity"))
            throw new InvalidOperationException("Auth:CredentialSource must be AzureCli or ManagedIdentity.");
        var identityId = source == "ManagedIdentity" ? Required("Auth:ManagedIdentityClientId") : null;
        return new AuthSettings(uri.GetLeftPart(UriPartial.Authority), Required("Auth:GoogleClientId"), source,
            identityId, Required("Cosmos:Endpoint"),
            Required("Cosmos:Database"), Required("Cosmos:Container"));
    }
}
