using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using OrderService;
using Xunit;

namespace OrderService.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public Mock<IOrderStore> Store { get; } = new(MockBehavior.Strict);
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Syntactically valid config only. Mock store ensures no credentials/network are used.
        Environment.SetEnvironmentVariable("DOCUMENTDB_URI", RulesAndSecurityTests.Config.Uri);
        Environment.SetEnvironmentVariable("DOCUMENTDB_ALLOWED_HOST", RulesAndSecurityTests.Config.AllowedHost);
        Environment.SetEnvironmentVariable("DOCUMENTDB_DATABASE", RulesAndSecurityTests.Config.Database);
        Environment.SetEnvironmentVariable("ORDER_API_TOKEN", RulesAndSecurityTests.Config.ApiToken);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOrderStore>();
            services.AddSingleton(Store.Object);
        });
    }
    public HttpClient OperatorClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RulesAndSecurityTests.Config.ApiToken);
        return client;
    }
}

public class ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    public ApiTests(ApiFactory factory) { this.factory = factory; factory.Store.Reset(); }
    private static HttpRequestMessage OrderMessage(string? key = "test-key", string body = "{\"items\":[{\"productId\":\"pen\",\"quantity\":1}]}")
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (key is not null) message.Headers.Add("Idempotency-Key", key);
        return message;
    }
    [Fact] public async Task AnonymousApiRejected()
    {
        var result = await factory.CreateClient().GetAsync("/api/inventory");
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal("unauthorized", (await result.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }
    [Fact] public async Task HealthIsNotDatabaseProof()
    {
        var result = await factory.CreateClient().GetStringAsync("/health");
        Assert.Contains("\"dependencyCheck\":false", result);
    }
    [Fact] public async Task SecurityHeadersAndRequestIdsPresent()
    {
        var result = await factory.CreateClient().GetAsync("/health");
        Assert.True(result.Headers.Contains("Content-Security-Policy"));
        Assert.Equal(32, result.Headers.GetValues("X-Request-Id").Single().Length);
        Assert.True(result.Headers.CacheControl!.NoStore);
    }
    [Fact] public async Task CrossOriginMutationRejected()
    {
        using var message = OrderMessage();
        message.Headers.Add("Origin", "https://untrusted.example");
        var result = await factory.OperatorClient().SendAsync(message);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
    }
    [Theory][InlineData(null)][InlineData("key,other")][InlineData("key with space")]
    public async Task MissingOrMalformedHeaderRejected(string? key)
    {
        var result = await factory.OperatorClient().SendAsync(OrderMessage(key));
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        factory.Store.VerifyNoOtherCalls();
    }
    [Fact] public async Task QueryKeyIsNotAccepted()
    {
        using var message = OrderMessage(null);
        message.RequestUri = new Uri("/api/orders?Idempotency-Key=query", UriKind.Relative);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.OperatorClient().SendAsync(message)).StatusCode);
    }
    [Theory][InlineData("not json")][InlineData("null")][InlineData("{\"items\":[]}")][InlineData("{\"items\":[{\"productId\":\"pen\",\"quantity\":0}]}")]
    [InlineData("{\"items\":[{\"productId\":\"pen\",\"quantity\":1}],\"failAfterFirst\":true}")]
    public async Task InvalidJsonOrOrderRejected(string body)
    {
        var result = await factory.OperatorClient().SendAsync(OrderMessage(body: body));
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        factory.Store.VerifyNoOtherCalls();
    }
    [Fact] public async Task NonJsonRejected()
    {
        using var message = OrderMessage();
        message.Content!.Headers.ContentType = new("text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await factory.OperatorClient().SendAsync(message)).StatusCode);
    }
    [Theory][InlineData(false, 201)][InlineData(true, 200)]
    public async Task CreateVersusReplayStatus(bool replay, int status)
    {
        var order = new Order("test-key", "order-id", "fingerprint", [new("pen", 1)], DateTime.UtcNow);
        factory.Store.Setup(s => s.PlaceAsync("test-key", It.IsAny<OrderRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OrderResult(order, replay));
        var result = await factory.OperatorClient().SendAsync(OrderMessage());
        Assert.Equal(status, (int)result.StatusCode);
        Assert.Equal(replay, (await result.Content.ReadFromJsonAsync<OrderResult>())!.Replayed);
    }
    [Theory][InlineData("out_of_stock", 409)][InlineData("idempotency_conflict", 409)][InlineData("outcome_unknown_retry_same_key", 503)]
    public async Task StableDomainErrors(string code, int status)
    {
        factory.Store.Setup(s => s.PlaceAsync(It.IsAny<string>(), It.IsAny<OrderRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OrderException(code, status));
        var result = await factory.OperatorClient().SendAsync(OrderMessage());
        Assert.Equal(status, (int)result.StatusCode);
        Assert.Equal(code, (await result.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }
    [Fact] public async Task RawErrorsNeverLeak()
    {
        factory.Store.Setup(s => s.PlaceAsync(It.IsAny<string>(), It.IsAny<OrderRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("secret-password-value"));
        var result = await factory.OperatorClient().SendAsync(OrderMessage());
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        Assert.DoesNotContain("secret", await result.Content.ReadAsStringAsync());
    }
    [Fact] public async Task PaginationPassesBoundedArguments()
    {
        factory.Store.Setup(s => s.InventoryAsync(2, "pen", It.IsAny<CancellationToken>())).ReturnsAsync(new Page<Product>([], null));
        Assert.Equal(HttpStatusCode.OK, (await factory.OperatorClient().GetAsync("/api/inventory?limit=2&cursor=pen")).StatusCode);
        factory.Store.VerifyAll();
    }
    [Theory][InlineData("/api/inventory?limit=101")][InlineData("/api/orders?cursor=a.b")]
    public async Task InvalidPaginationRejectedBeforeStore(string path)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.OperatorClient().GetAsync(path)).StatusCode);
        factory.Store.VerifyNoOtherCalls();
    }
    [Fact] public async Task NoResetRoute() => Assert.Equal(HttpStatusCode.NotFound, (await factory.OperatorClient().PostAsync("/api/reset", null)).StatusCode);
}
