using System.Net;
using System.Text;
using Khata.Api;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Moq;

namespace Khata.UnitTests;

public class HealthFunctionTests
{
    [Fact]
    public async Task HealthReturnsJsonSuccess()
    {
        var context = new Mock<FunctionContext>().Object;
        using var body = new MemoryStream();
        var response = new Mock<HttpResponseData>(context);
        response.SetupProperty(r => r.StatusCode);
        response.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection());
        response.SetupGet(r => r.Body).Returns(body);
        var request = new Mock<HttpRequestData>(context);
        request.Setup(r => r.CreateResponse()).Returns(response.Object);

        var result = await new HealthFunction().Run(request.Object);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("application/json; charset=utf-8", result.Headers.GetValues("Content-Type").Single());
        Assert.Equal("{\"status\":\"ok\"}", Encoding.UTF8.GetString(body.ToArray()));
    }
}
