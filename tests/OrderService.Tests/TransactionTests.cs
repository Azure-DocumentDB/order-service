using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using OrderService;
using Xunit;

namespace OrderService.Tests;

// These verify transaction/session/filter wiring, not database isolation. Real races belong to Probes.
public class TransactionTests
{
    private sealed class Fixture
    {
        public readonly Mock<IMongoClient> Client = new();
        public readonly Mock<IMongoDatabase> Database = new();
        public readonly Mock<IMongoCollection<Product>> Inventory = new();
        public readonly Mock<IMongoCollection<Order>> Orders = new();
        public readonly Mock<IClientSessionHandle> Session = new();
        public readonly Mock<ITransactionObserver> Observer = new();
        public readonly List<BsonDocument> Filters = [];
        public readonly List<BsonDocument> Updates = [];
        public readonly List<Order> Inserted = [];
        public TransactionOptions? Options;
        public CancellationToken TransactionToken;
        public Fixture(Order? existing = null)
        {
            Client.Setup(c => c.GetDatabase(It.IsAny<string>(), It.IsAny<MongoDatabaseSettings>())).Returns(Database.Object);
            Database.Setup(d => d.GetCollection<Product>("inventory", It.IsAny<MongoCollectionSettings>())).Returns(Inventory.Object);
            Database.Setup(d => d.GetCollection<Order>("orders", It.IsAny<MongoCollectionSettings>())).Returns(Orders.Object);
            Client.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(Session.Object);
            var cursor = new Mock<IAsyncCursor<Order>>();
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true).ReturnsAsync(false);
            cursor.SetupGet(c => c.Current).Returns(existing is null ? [] : [existing]);
            Orders.Setup(o => o.FindAsync(Session.Object, It.IsAny<FilterDefinition<Order>>(), It.IsAny<FindOptions<Order, Order>>(), It.IsAny<CancellationToken>())).ReturnsAsync(cursor.Object);
            Orders.Setup(o => o.InsertOneAsync(Session.Object, It.IsAny<Order>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()))
                .Callback<IClientSessionHandle, Order, InsertOneOptions, CancellationToken>((_, order, _, _) => Inserted.Add(order)).Returns(Task.CompletedTask);
            Inventory.Setup(i => i.UpdateOneAsync(Session.Object, It.IsAny<FilterDefinition<Product>>(), It.IsAny<UpdateDefinition<Product>>(), It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()))
                .Callback<IClientSessionHandle, FilterDefinition<Product>, UpdateDefinition<Product>, UpdateOptions, CancellationToken>((_, filter, update, _, _) =>
                {
                    var render = new RenderArgs<Product>(BsonSerializer.SerializerRegistry.GetSerializer<Product>(), BsonSerializer.SerializerRegistry);
                    Filters.Add(filter.Render(render)); Updates.Add(update.Render(render).AsBsonDocument);
                }).ReturnsAsync(new UpdateResult.Acknowledged(1, 1, null));
            Observer.Setup(o => o.AfterReservationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Session.Setup(s => s.WithTransactionAsync(It.IsAny<Func<IClientSessionHandle, CancellationToken, Task<OrderResult>>>(), It.IsAny<TransactionOptions>(), It.IsAny<CancellationToken>()))
                .Returns((Func<IClientSessionHandle, CancellationToken, Task<OrderResult>> callback, TransactionOptions options, CancellationToken ct) =>
                {
                    Options = options; TransactionToken = ct; return callback(Session.Object, ct);
                });
        }
        public MongoOrderStore Store() => new(Client.Object, RulesAndSecurityTests.Config, Observer.Object);
    }
    [Fact] public async Task AllWritesUseSameSessionAndConditionalDecrements()
    {
        var fixture = new Fixture();
        var result = await fixture.Store().PlaceAsync("key", new([new("b", 2), new("a", 1)]), CancellationToken.None);
        Assert.False(result.Replayed);
        Assert.Single(fixture.Inserted);
        Assert.Equal("key", fixture.Inserted[0].Id);
        Assert.Equal(2, fixture.Filters.Count);
        Assert.Equal("a", fixture.Filters[0]["_id"].AsString);
        Assert.Equal(1, fixture.Filters[0]["Stock"]["$gte"].AsInt32);
        Assert.Equal(-1, fixture.Updates[0]["$inc"]["Stock"].AsInt32);
        Assert.Equal(-2, fixture.Updates[1]["$inc"]["Stock"].AsInt32);
        Assert.Equal(ReadConcern.Snapshot, fixture.Options!.ReadConcern);
        Assert.Equal(WriteConcern.WMajority, fixture.Options.WriteConcern);
        Assert.Equal(TimeSpan.FromSeconds(5), fixture.Options.MaxCommitTime);
        Assert.True(fixture.TransactionToken.CanBeCanceled);
    }
    [Fact] public async Task ExistingKeySkipsAllWrites()
    {
        var request = new OrderRequest([new("a", 1)]);
        var validated = OrderRules.Validate(request);
        var fixture = new Fixture(new Order("key", "id", validated.Fingerprint, validated.Items, DateTime.UtcNow));
        Assert.True((await fixture.Store().PlaceAsync("key", request, CancellationToken.None)).Replayed);
        Assert.Empty(fixture.Inserted);
        Assert.Empty(fixture.Filters);
    }
    [Fact] public async Task ExistingKeyMismatchSkipsAllWrites()
    {
        var fixture = new Fixture(new Order("key", "id", "different", [new("a", 1)], DateTime.UtcNow));
        Assert.Equal("idempotency_conflict", (await Assert.ThrowsAsync<OrderException>(() => fixture.Store().PlaceAsync("key", new([new("a", 1)]), CancellationToken.None))).Code);
        Assert.Empty(fixture.Inserted);
        Assert.Empty(fixture.Filters);
    }
    [Fact] public async Task ObserverFailureEscapesCallbackForDriverAbort()
    {
        var fixture = new Fixture();
        fixture.Observer.Setup(o => o.AfterReservationAsync(1, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("injected"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().PlaceAsync("key", new([new("a", 1), new("b", 1)]), CancellationToken.None));
        Assert.Single(fixture.Filters);
    }
    [Fact] public async Task NoMatchingStockEscapesCallbackForDriverAbort()
    {
        var fixture = new Fixture();
        fixture.Inventory.Setup(i => i.UpdateOneAsync(fixture.Session.Object, It.IsAny<FilterDefinition<Product>>(), It.IsAny<UpdateDefinition<Product>>(), It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResult.Acknowledged(0, 0, null));
        Assert.Equal("out_of_stock", (await Assert.ThrowsAsync<OrderException>(() => fixture.Store().PlaceAsync("key", new([new("a", 1)]), CancellationToken.None))).Code);
        fixture.Observer.Verify(o => o.AfterReservationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact] public async Task TransientLabelsEscapeCallbackIntactForSdkRetry()
    {
        var fixture = new Fixture();
        var error = new MongoException("synthetic"); error.AddErrorLabel("TransientTransactionError");
        fixture.Observer.Setup(o => o.AfterReservationAsync(1, It.IsAny<CancellationToken>())).ThrowsAsync(error);
        var actual = await Assert.ThrowsAsync<MongoException>(() => fixture.Store().PlaceAsync("key", new([new("a", 1)]), CancellationToken.None));
        Assert.Same(error, actual);
        Assert.True(actual.HasErrorLabel("TransientTransactionError"));
    }
}
