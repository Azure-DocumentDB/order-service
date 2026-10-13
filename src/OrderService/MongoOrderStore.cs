using MongoDB.Driver;

namespace OrderService;

public sealed class MongoOrderStore : IOrderStore
{
    private readonly IMongoClient client;
    private readonly IMongoCollection<Product> inventory;
    private readonly IMongoCollection<Order> orders;
    private readonly AppConfig config;
    private readonly ITransactionObserver observer;
    public MongoOrderStore(IMongoClient client, AppConfig config, ITransactionObserver observer)
    {
        config.Validate(false);
        this.client = client;
        this.config = config;
        this.observer = observer;
        var db = client.GetDatabase(config.Database);
        inventory = db.GetCollection<Product>("inventory");
        orders = db.GetCollection<Order>("orders");
    }

    public async Task<OrderResult> PlaceAsync(string key, OrderRequest request, CancellationToken cancellationToken)
    {
        config.DemandWrites();
        OrderRules.ValidateKey(key);
        var validated = OrderRules.Validate(request);
        // Full driver retry loop bounded independently of the HTTP connection. WithTransactionAsync
        // retries TransientTransactionError; UnknownTransactionCommitResult retries the SAME commit.
        // Never catch Mongo exceptions in the callback: the driver needs labels to abort/retry correctly.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var order = new Order(key, Guid.NewGuid().ToString("N"), validated.Fingerprint, validated.Items, DateTime.UtcNow);
        using var session = await client.StartSessionAsync(cancellationToken: budget.Token);
        try
        {
            return await session.WithTransactionAsync(async (s, ct) =>
            {
                var existing = await orders.Find(s, o => o.Id == key).FirstOrDefaultAsync(ct);
                if (existing is not null) return OrderRules.Replay(existing, validated);
                // _id is the unique, immutable idempotency record, in the same transaction as stock.
                // Insert before touching inventory to contend on a repeated key early.
                await orders.InsertOneAsync(s, order, cancellationToken: ct);
                var completed = 0;
                foreach (var item in validated.Items)
                {
                    var filter = Builders<Product>.Filter.Eq(p => p.Id, item.ProductId) &
                                 Builders<Product>.Filter.Gte(p => p.Stock, item.Quantity);
                    var changed = await inventory.UpdateOneAsync(s, filter,
                        Builders<Product>.Update.Inc(p => p.Stock, -item.Quantity), cancellationToken: ct);
                    if (changed.ModifiedCount != 1) throw new OrderException("out_of_stock", 409);
                    await observer.AfterReservationAsync(++completed, ct);
                }
                return new OrderResult(order, false);
            }, new TransactionOptions(readConcern: ReadConcern.Snapshot, readPreference: ReadPreference.Primary,
                writeConcern: WriteConcern.WMajority, maxCommitTime: TimeSpan.FromSeconds(5)), budget.Token);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A committed concurrent request may win the unique-key race without a transient label.
            // Resolve outside the aborted transaction; never fabricate a successful response.
            return await ResolveAsync(key, validated);
        }
        catch (MongoException ex) when (ex.HasErrorLabel("UnknownTransactionCommitResult"))
        {
            return await ResolveAsync(key, validated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancellation can occur while commit acknowledgement is in flight. Lookup with a
            // separate short budget. If absent/unreachable, outcome is UNKNOWN, not "failed".
            return await ResolveAsync(key, validated);
        }
    }

    private async Task<OrderResult> ResolveAsync(string key, ValidatedOrder request)
    {
        using var readBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var existing = await orders.WithReadConcern(ReadConcern.Majority).WithReadPreference(ReadPreference.Primary)
                .Find(o => o.Id == key).FirstOrDefaultAsync(readBudget.Token);
            if (existing is not null) return OrderRules.Replay(existing, request);
        }
        catch (MongoException) { }
        catch (OperationCanceledException) { }
        throw new OrderException("outcome_unknown_retry_same_key", 503);
    }

    public async Task<Page<Product>> InventoryAsync(int limit, string? cursor, CancellationToken cancellationToken)
    {
        var filter = cursor is null ? Builders<Product>.Filter.Empty : Builders<Product>.Filter.Gt(p => p.Id, cursor);
        var rows = await inventory.Find(filter).SortBy(p => p.Id).Limit(limit + 1).ToListAsync(cancellationToken);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<Page<Order>> OrdersAsync(int limit, string? cursor, CancellationToken cancellationToken)
    {
        var filter = cursor is null ? Builders<Order>.Filter.Empty : Builders<Order>.Filter.Gt(o => o.Id, cursor);
        var rows = await orders.Find(filter).SortBy(o => o.Id).Limit(limit + 1).ToListAsync(cancellationToken);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        config.DemandWrites();
        var db = client.GetDatabase(config.Database);
        // Create collections before any transaction. Existing collections, stock and orders are kept.
        foreach (var name in new[] { "inventory", "orders" })
        {
            try { await db.CreateCollectionAsync(name, cancellationToken: cancellationToken); }
            catch (MongoCommandException ex) when (ex.Code == 48) { }
        }
        foreach (var product in new[] { new Product("notebook", "Synthetic notebook", 50), new Product("pen", "Synthetic pen", 100) })
            await inventory.UpdateOneAsync(p => p.Id == product.Id,
                Builders<Product>.Update.SetOnInsert(p => p.Name, product.Name).SetOnInsert(p => p.Stock, product.Stock),
                new UpdateOptions { IsUpsert = true }, cancellationToken);
    }
}
