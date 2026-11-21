using System.Collections.Concurrent;
using Khata.Application;

namespace Khata.UnitTests;

public class AuthenticationServiceTests
{
    [Fact]
    public async Task LoginBindsFlowAndPreventsReplay()
    {
        var (service, store, _, _) = CreateService();
        var start = await service.StartAsync(default);
        Assert.Equal(AuthenticationService.Hash(start.Nonce), store.Flows.Values.Single().NonceHash);

        Assert.Null(await service.CompleteAsync(start.State, new string('a', 43), "token", null, default));
        var result = await service.CompleteAsync(start.State, start.BrowserBinding, "token", null, default);
        Assert.NotNull(result);
        Assert.Null(await service.CompleteAsync(start.State, start.BrowserBinding, "token", null, default));
        Assert.NotNull(await service.GetSessionAsync(result.SessionToken, default));
        Assert.DoesNotContain(result.SessionToken, store.Sessions.Keys);
    }

    [Fact]
    public async Task ExpiredFlowAndSessionAreRejected()
    {
        var (service, _, _, clock) = CreateService();
        var start = await service.StartAsync(default);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(await service.CompleteAsync(start.State, start.BrowserBinding, "token", null, default));

        start = await service.StartAsync(default);
        var result = await service.CompleteAsync(start.State, start.BrowserBinding, "token", null, default);
        clock.Advance(TimeSpan.FromHours(8));
        Assert.Null(await service.GetSessionAsync(result!.SessionToken, default));
    }

    [Fact]
    public async Task RepeatLoginKeepsUserAndRotatesSessionAndLogoutRequiresCsrf()
    {
        var (service, _, _, _) = CreateService();
        var firstStart = await service.StartAsync(default);
        var first = await service.CompleteAsync(firstStart.State, firstStart.BrowserBinding, "token", null, default);
        var firstSession = await service.GetSessionAsync(first!.SessionToken, default);
        var secondStart = await service.StartAsync(default);
        var second = await service.CompleteAsync(secondStart.State, secondStart.BrowserBinding, "token", first.SessionToken, default);
        var secondSession = await service.GetSessionAsync(second!.SessionToken, default);
        Assert.Equal(firstSession!.UserId, secondSession!.UserId);
        Assert.Null(await service.GetSessionAsync(first.SessionToken, default));
        Assert.False(await service.LogoutAsync(second.SessionToken, "wrong", default));
        Assert.NotNull(await service.GetSessionAsync(second.SessionToken, default));
        Assert.True(await service.LogoutAsync(second.SessionToken, AuthenticationService.CsrfToken(second.SessionToken), default));
        Assert.Null(await service.GetSessionAsync(second.SessionToken, default));
    }

    private static (AuthenticationService, FakeStore, FakeProvider, FakeClock) CreateService()
    {
        var store = new FakeStore();
        var provider = new FakeProvider();
        var clock = new FakeClock();
        return (new AuthenticationService(store, provider, clock), store, provider, clock);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class FakeProvider : IIdentityProvider
    {
        public Task<ExternalIdentity> ValidateTokenAsync(string token, string nonceHash, CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalIdentity("google", "https://accounts.google.com", "subject"));
    }

    private sealed class FakeStore : IAuthenticationStore
    {
        public ConcurrentDictionary<string, LoginFlow> Flows { get; } = new();
        public ConcurrentDictionary<string, UserSession> Sessions { get; } = new();
        private readonly ConcurrentDictionary<ExternalIdentity, UserIdentity> users = new();

        public Task SaveFlowAsync(LoginFlow flow, CancellationToken cancellationToken)
        {
            Assert.True(Flows.TryAdd(flow.Id, flow));
            return Task.CompletedTask;
        }

        public Task<LoginFlow?> ConsumeFlowAsync(string id, string bindingHash, CancellationToken cancellationToken)
        {
            if (Flows.TryGetValue(id, out var flow) && AuthenticationService.FixedEquals(flow.BindingHash, bindingHash))
                Flows.TryRemove(id, out flow);
            else
                flow = null;
            return Task.FromResult(flow);
        }

        public Task<UserIdentity> GetOrCreateUserAsync(ExternalIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult(users.GetOrAdd(identity, key => new UserIdentity(Guid.NewGuid().ToString("N"), key)));

        public Task SaveSessionAsync(UserSession session, CancellationToken cancellationToken)
        {
            Assert.True(Sessions.TryAdd(session.Id, session));
            return Task.CompletedTask;
        }

        public Task<UserSession?> GetSessionAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.GetValueOrDefault(id));

        public Task DeleteSessionAsync(string id, CancellationToken cancellationToken)
        {
            Sessions.TryRemove(id, out _);
            return Task.CompletedTask;
        }
    }
}
