using System.Security.Cryptography;
using System.Text;

namespace Khata.Application;

public record ExternalIdentity(string Provider, string Issuer, string Subject);
public record LoginFlow(string Id, string BindingHash, string NonceHash, DateTimeOffset ExpiresAt);
public record UserIdentity(string Id, ExternalIdentity Identity);
public record UserSession(string Id, string UserId, DateTimeOffset ExpiresAt);
public record LoginStart(string State, string Nonce, string BrowserBinding);
public record LoginResult(string SessionToken);

public interface IIdentityProvider
{
    Task<ExternalIdentity> ValidateTokenAsync(string token, string nonceHash, CancellationToken cancellationToken);
}

public interface IAuthenticationStore
{
    Task SaveFlowAsync(LoginFlow flow, CancellationToken cancellationToken);
    Task<LoginFlow?> ConsumeFlowAsync(string id, string bindingHash, CancellationToken cancellationToken);
    Task<UserIdentity> GetOrCreateUserAsync(ExternalIdentity identity, CancellationToken cancellationToken);
    Task SaveSessionAsync(UserSession session, CancellationToken cancellationToken);
    Task<UserSession?> GetSessionAsync(string id, CancellationToken cancellationToken);
    Task DeleteSessionAsync(string id, CancellationToken cancellationToken);
}

public sealed class AuthenticationService(IAuthenticationStore store, IIdentityProvider provider, TimeProvider clock)
{
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    public async Task<LoginStart> StartAsync(CancellationToken cancellationToken)
    {
        var state = NewToken();
        var binding = NewToken();
        var nonce = NewToken();
        await store.SaveFlowAsync(new LoginFlow(
            Hash(state), Hash(binding), Hash(nonce), clock.GetUtcNow().Add(FlowLifetime)), cancellationToken);
        return new LoginStart(state, nonce, binding);
    }

    public async Task<LoginResult?> CompleteAsync(string state, string binding, string credential, string? oldSession, CancellationToken cancellationToken)
    {
        if (!IsToken(state) || !IsToken(binding) || string.IsNullOrWhiteSpace(credential) || credential.Length > 16384)
            return null;

        var flow = await store.ConsumeFlowAsync(Hash(state), Hash(binding), cancellationToken);
        if (flow is null || flow.ExpiresAt <= clock.GetUtcNow())
            return null;

        var identity = await provider.ValidateTokenAsync(credential, flow.NonceHash, cancellationToken);
        var user = await store.GetOrCreateUserAsync(identity, cancellationToken);
        if (IsToken(oldSession))
            await store.DeleteSessionAsync(Hash(oldSession!), cancellationToken);

        var token = NewToken();
        await store.SaveSessionAsync(new UserSession(
            Hash(token), user.Id, clock.GetUtcNow().Add(SessionLifetime)), cancellationToken);
        return new LoginResult(token);
    }

    public async Task<UserSession?> GetSessionAsync(string? token, CancellationToken cancellationToken)
    {
        if (!IsToken(token))
            return null;
        var session = await store.GetSessionAsync(Hash(token!), cancellationToken);
        return session is not null && session.ExpiresAt > clock.GetUtcNow() ? session : null;
    }

    public async Task<bool> LogoutAsync(string? token, string? csrfToken, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(token, cancellationToken);
        if (session is null || !FixedEquals(CsrfToken(token!), csrfToken))
            return false;
        await store.DeleteSessionAsync(session.Id, cancellationToken);
        return true;
    }

    public static bool FixedEquals(string expected, string? actual) =>
        actual is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    public static string Hash(string value) =>
        Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string CsrfToken(string sessionToken) => Hash("csrf:" + sessionToken);

    private static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool IsToken(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
