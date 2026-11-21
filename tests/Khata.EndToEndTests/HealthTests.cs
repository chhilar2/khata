using System.Net;
using System.Net.Http.Json;

namespace Khata.EndToEndTests;

public class HealthTests
{
    [Fact]
    public async Task HealthEndpointReturnsOk()
    {
        var baseUrl = Environment.GetEnvironmentVariable("KHATA_API_BASE_URL")
            ?? throw new InvalidOperationException("Set KHATA_API_BASE_URL to the running Functions host URL.");
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };

        using var response = await client.GetAsync("api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(body);
        Assert.Equal("ok", body.Status);
    }

    private sealed record HealthResponse(string Status);
}
