using Khata.Api;
using Khata.Application;
using Khata.Infrastructure;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var builder = FunctionsApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuthSettings>(services => AuthSettings.Load(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }));
builder.Services.AddSingleton<IIdentityProvider>(services =>
{
    var settings = services.GetRequiredService<AuthSettings>();
    return new GoogleIdentityProvider(settings.GoogleClientId, services.GetRequiredService<HttpClient>());
});
builder.Services.AddSingleton(services =>
{
    var settings = services.GetRequiredService<AuthSettings>();
    TokenCredential credential = settings.CredentialSource == "AzureCli"
        ? new AzureCliCredential()
        : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId!));
    return new CosmosClient(settings.CosmosEndpoint, credential, new CosmosClientOptions
    {
        ConsistencyLevel = ConsistencyLevel.Strong,
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
            IgnoreNullValues = true
        }
    });
});
builder.Services.AddSingleton<IAuthenticationStore>(services =>
{
    var settings = services.GetRequiredService<AuthSettings>();
    return new CosmosAuthenticationStore(services.GetRequiredService<CosmosClient>()
        .GetContainer(settings.CosmosDatabase, settings.CosmosContainer),
        services.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<IAccountStore>(services =>
{
    var settings = services.GetRequiredService<AuthSettings>();
    return new CosmosAccountStore(services.GetRequiredService<CosmosClient>()
        .GetContainer(settings.CosmosDatabase, settings.CosmosContainer),
        services.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<IManagedProfileStore>(services =>
{
    var settings = services.GetRequiredService<AuthSettings>();
    return new CosmosManagedProfileStore(services.GetRequiredService<CosmosClient>()
        .GetContainer(settings.CosmosDatabase, "managed-profiles"),
        services.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<AuthenticationService>();
builder.Build().Run();
