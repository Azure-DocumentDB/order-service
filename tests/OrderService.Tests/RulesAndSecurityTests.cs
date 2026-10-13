using Azure.Core;
using Moq;
using MongoDB.Driver;
using OrderService;
using Xunit;

namespace OrderService.Tests;

public class RulesAndSecurityTests
{
    internal static AppConfig Config => new("mongodb+srv://sample.mongocluster.cosmos.azure.com/?tls=true&retrywrites=false&authSource=admin",
        "sample.mongocluster.cosmos.azure.com", "orders_demo_test", true, true, new string('a', 40));

    [Theory]
    [InlineData("")][InlineData("a b")][InlineData("a,b")][InlineData("a/b")][InlineData("ü")][InlineData("a\r\nb")]
    public void BadKeysRejected(string key) => Assert.Equal("invalid_idempotency_key", Assert.Throws<OrderException>(() => OrderRules.ValidateKey(key)).Code);
    [Fact] public void KeyLengthBounded() => Assert.Throws<OrderException>(() => OrderRules.ValidateKey(new string('a', 65)));
    [Fact] public void KeyBoundaryAccepted() => OrderRules.ValidateKey(new string('a', 64));
    [Fact] public void FingerprintCanonicalizesLineOrdering()
    {
        var a = OrderRules.Validate(new([new("a", 1), new("b", 2)]));
        var b = OrderRules.Validate(new([new("b", 2), new("a", 1)]));
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal("a", a.Items[0].ProductId);
        Assert.NotEqual(a.Fingerprint, OrderRules.Validate(new([new("a", 2), new("b", 2)])).Fingerprint);
    }
    [Fact] public void NullEmptyOversizedAndDuplicateItemsRejected()
    {
        foreach (var request in new OrderRequest?[] { null, new(null), new([]), new([new("a", 1), new("a", 2)]),
                     new(Enumerable.Range(0, 9).Select(i => new LineItem("p" + i, 1)).ToArray()), new([null!]) })
            Assert.Throws<OrderException>(() => OrderRules.Validate(request));
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(101)][InlineData(int.MaxValue)]
    public void BadQuantityRejected(int quantity) => Assert.Throws<OrderException>(() => OrderRules.Validate(new([new("p", quantity)])));
    [Theory][InlineData("")][InlineData("x:y")][InlineData("$where")][InlineData("a.b")][InlineData("a\nb")]
    public void ProductIdentifierBounded(string id) => Assert.Throws<OrderException>(() => OrderRules.Validate(new([new(id, 1)])));
    [Fact] public void ReplayChecksFingerprint()
    {
        var validated = OrderRules.Validate(new([new("a", 1)]));
        var order = new Order("key", "order", validated.Fingerprint, validated.Items, DateTime.UtcNow);
        Assert.True(OrderRules.Replay(order, validated).Replayed);
        Assert.Equal("idempotency_conflict", Assert.Throws<OrderException>(() => OrderRules.Replay(order, OrderRules.Validate(new([new("a", 2)])))).Code);
    }
    [Theory][InlineData("0", null)][InlineData("101", null)][InlineData("bad", null)][InlineData("1", "a/b")]
    public void PaginationRejectsBadValues(string limit, string? cursor) => Assert.Throws<OrderException>(() => OrderRules.Page(limit, cursor));
    [Fact] public void PaginationDefaultsAndBounds() { Assert.Equal((25, (string?)null), OrderRules.Page(null, null)); Assert.Equal((100, "x"), OrderRules.Page("100", "x")); }

    [Theory]
    [InlineData("mongodb://sample.mongocluster.cosmos.azure.com")]
    [InlineData("mongodb+srv://localhost")]
    [InlineData("mongodb+srv://sample.mongocluster.cosmos.azure.com.evil.test")]
    [InlineData("mongodb+srv://u:p@sample.mongocluster.cosmos.azure.com")]
    [InlineData("mongodb+srv://sample.mongocluster.cosmos.azure.com:27017")]
    [InlineData("mongodb+srv://sample.mongocluster.cosmos.azure.com/db")]
    [InlineData("mongodb+srv://sample.mongocluster.cosmos.azure.com/#fragment")]
    [InlineData("mongodb+srv://sample.mongocluster.cosmos.azure.com/?tls=false")]
    [InlineData("mongodb+srv://sample.mongocluster.cosmos.azure.com/?tlsAllowInvalidCertificates=true")]
    [InlineData("mongodb+srv://sample.mongocluster.cosmos.azure.com/?authMechanismProperties=TOKEN_RESOURCE:evil")]
    [InlineData("mongodb+srv://sample.mongocluster.cosmos.azure.com/?tls=true&TLS=true")]
    [InlineData("mongodb+srv://sample.mongocluster.cosmos.azure.com/?retrywrites=true")]
    public void UnsafeDestinationsRejectedBeforeCredentials(string uri)
    {
        var calls = 0;
        Assert.Throws<ConfigurationException>(() => ClientFactory.Settings(Config with { Uri = uri }, () => { calls++; return Mock.Of<TokenCredential>(); }));
        Assert.Equal(0, calls);
    }
    [Fact] public void ExactHostRequired() => Assert.Throws<ConfigurationException>(() => (Config with { AllowedHost = "other.mongocluster.cosmos.azure.com" }).Validate());
    [Theory][InlineData("prod")][InlineData("orders_demo_")][InlineData("orders_demo_prod.foo")][InlineData("orders_demo_UPPER")]
    public void DisposableDatabaseRequired(string database) => Assert.Throws<ConfigurationException>(() => (Config with { Database = database }).Validate());
    [Fact] public void WritesRequireBothExplicitGuards()
    {
        Assert.Throws<ConfigurationException>(() => (Config with { AllowWrites = false }).DemandWrites());
        Assert.Throws<ConfigurationException>(() => (Config with { SingleShard = false }).DemandWrites());
    }
    [Fact] public void ApiTokenBoundedAndNeverImplicit()
    {
        foreach (var token in new[] { "", new string('x', 31), new string('x', 257), new string('x', 32) + " " })
            Assert.Throws<ConfigurationException>(() => (Config with { ApiToken = token }).Validate());
    }
    [Fact] public void ScramRequiresSeparateCredentials()
    {
        Assert.Throws<ConfigurationException>(() => (Config with { Auth = "scram" }).Validate());
        Assert.Throws<ConfigurationException>(() => (Config with { Auth = "bad" }).Validate());
        (Config with { Auth = "scram", Username = "operator", Password = "test-only" }).Validate();
    }
    [Fact] public void SafeSettingsForceTlsAndDisableRetryWrites()
    {
        var settings = ClientFactory.Settings(Config, () => Mock.Of<TokenCredential>());
        Assert.True(settings.UseTls);
        Assert.False(settings.AllowInsecureTls);
        Assert.False(settings.RetryWrites);
        Assert.Equal("MONGODB-OIDC", settings.Credential.Mechanism);
    }
    [Fact] public async Task OidcUsesFixedAudienceAndRealLifetime()
    {
        var mock = new Mock<TokenCredential>();
        mock.Setup(c => c.GetTokenAsync(It.Is<TokenRequestContext>(x => x.Scopes.SequenceEqual(new[] { DatabaseTokenCallback.Scope })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken("synthetic-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        await new DatabaseTokenCallback(mock.Object).GetOidcAccessTokenAsync(null!, CancellationToken.None);
        mock.VerifyAll();
    }
    [Fact] public async Task OidcRejectsExpiredTokens()
    {
        var mock = new Mock<TokenCredential>();
        mock.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken("synthetic-token", DateTimeOffset.UtcNow.AddSeconds(-1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseTokenCallback(mock.Object).GetOidcAccessTokenAsync(null!, CancellationToken.None));
    }
}
