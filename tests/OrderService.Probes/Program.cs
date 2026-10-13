using System.Text.Json;
using MongoDB.Driver;
using OrderService;

var checks = new List<string>();
var stage = "configuration";
try
{
    if (args is not ["--live"] || Environment.GetEnvironmentVariable("RUN_LIVE_TESTS") != "true")
        throw new ProbeFailure("live_opt_in_required");
    var config = AppConfig.FromEnvironment();
    config.DemandWrites();
    using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var ct = lifetime.Token;
    var client = new MongoClient(ClientFactory.Settings(config));
    var store = new MongoOrderStore(client, config, new NoopTransactionObserver());
    stage = "initial_seed";
    await store.SeedAsync(ct);
    stage = "fixture_setup";
    var db = client.GetDatabase(config.Database);
    var inventory = db.GetCollection<Product>("inventory");
    var orders = db.GetCollection<Order>("orders");
    var run = Guid.NewGuid().ToString("N")[..12];
    var stats = new List<object>();
    void Check(bool condition, string name)
    {
        if (!condition) throw new ProbeFailure(name);
        checks.Add(name);
    }
    async Task<Product> Stock(string id) => await inventory.Find(p => p.Id == id).SingleAsync(ct);
    async Task<long> OrderCount(string key) => await orders.CountDocumentsAsync(o => o.Id == key, cancellationToken: ct);
    async Task ExpectCode(Func<Task> operation, string code)
    {
        try { await operation(); throw new ProbeFailure("expected_" + code); }
        catch (OrderException ex) when (ex.Code == code) { }
    }
    // Each probe creates uniquely named synthetic products. No drop/reset/delete is ever used.
    foreach (var concurrency in new[] { 20, 100 })
    {
        stage = $"race_{concurrency}_fixture_insert";
        var product = $"race_{concurrency}_{run}";
        await inventory.InsertOneAsync(new Product(product, "Synthetic race fixture", 5), cancellationToken: ct);
        using var start = new SemaphoreSlim(0, concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            await start.WaitAsync(ct);
            var key = $"buy_{concurrency}_{run}_{i:D3}";
            var request = new OrderRequest([new(product, 1)]);
            // Only same-key retry on bounded transient/unknown outcomes, never manufacture a new key.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try { return (Key: key, Success: !(await store.PlaceAsync(key, request, ct)).Replayed, SoldOut: false); }
                catch (OrderException ex) when (ex.Code == "out_of_stock") { return (Key: key, Success: false, SoldOut: true); }
                catch (OrderException ex) when (ex.Status == 503 && attempt < 3) { await Task.Delay(100 * (attempt + 1), ct); }
                catch (MongoException ex) when (ex.HasErrorLabel("TransientTransactionError") && attempt < 3) { await Task.Delay(100 * (attempt + 1), ct); }
            }
            throw new ProbeFailure("race_retry_budget_exhausted");
        }).ToArray();
        stage = $"race_{concurrency}_transactions";
        start.Release(concurrency);
        var outcomes = await Task.WhenAll(tasks);
        stage = $"race_{concurrency}_verification";
        var persisted = await orders.Find(Builders<Order>.Filter.AnyEq("Items.ProductId", product)).ToListAsync(ct);
        var stock = (await Stock(product)).Stock;
        Check(outcomes.Count(x => !x.SoldOut) == 5, $"race_{concurrency}_five_successes");
        Check(outcomes.Count(x => x.SoldOut) == concurrency - 5, $"race_{concurrency}_remaining_sold_out");
        Check(stock == 0, $"race_{concurrency}_stock_zero_never_negative");
        Check(persisted.Count == 5 && persisted.Select(o => o.Id).Distinct().Count() == 5, $"race_{concurrency}_five_unique_orders");
        stats.Add(new { concurrency, succeeded = 5, soldOut = concurrency - 5, finalStock = stock, persistedOrders = persisted.Count });
    }
    stage = "atomic_fixture_setup";
    var a = "atomic_a_" + run;
    var b = "atomic_b_" + run;
    await inventory.InsertManyAsync([new Product(a, "Synthetic A", 10), new Product(b, "Synthetic B", 10)], cancellationToken: ct);
    var payload = new OrderRequest([new(a, 2), new(b, 3)]);
    var key1 = "replay_" + run;
    stage = "sequential_replay";
    var first = await store.PlaceAsync(key1, payload, ct);
    var replay = await store.PlaceAsync(key1, new OrderRequest([new(b, 3), new(a, 2)]), ct);
    Check(!first.Replayed && replay.Replayed && first.Order.OrderId == replay.Order.OrderId, "replay_returns_same_order");
    Check((await Stock(a)).Stock == 8 && (await Stock(b)).Stock == 7 && await OrderCount(key1) == 1, "replay_deducts_once");
    await ExpectCode(() => store.PlaceAsync(key1, new OrderRequest([new(a, 1)]), ct), "idempotency_conflict");
    Check((await Stock(a)).Stock == 8 && (await Stock(b)).Stock == 7, "mismatch_changes_nothing");
    var duplicateKey = "concurrent_replay_" + run;
    stage = "concurrent_same_key";
    var duplicates = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.PlaceAsync(duplicateKey, new OrderRequest([new(a, 1)]), ct)));
    Check(duplicates.Count(x => !x.Replayed) == 1 && duplicates.Select(x => x.Order.OrderId).Distinct().Count() == 1, "concurrent_same_key_one_order");
    Check((await Stock(a)).Stock == 7 && await OrderCount(duplicateKey) == 1, "concurrent_same_key_deducts_once");
    stage = "new_key";
    var newKey = "new_" + run;
    await store.PlaceAsync(newKey, payload, ct);
    Check((await Stock(a)).Stock == 5 && (await Stock(b)).Stock == 4, "new_key_is_new_purchase");
    stage = "injected_abort";
    var failing = new MongoOrderStore(client, config, new FailAfterFirstReservation());
    var abortKey = "abort_" + run;
    try { await failing.PlaceAsync(abortKey, payload, ct); throw new ProbeFailure("abort_not_injected"); }
    catch (InjectedAbort) { }
    Check((await Stock(a)).Stock == 5 && (await Stock(b)).Stock == 4 && await OrderCount(abortKey) == 0, "injected_abort_rolls_back_first_stock_and_order");
    stage = "second_product_rollback";
    await ExpectCode(() => store.PlaceAsync("soldout_" + run, new OrderRequest([new(a, 1), new(b, 100)]), ct), "out_of_stock");
    Check((await Stock(a)).Stock == 5 && (await Stock(b)).Stock == 4 && await OrderCount("soldout_" + run) == 0, "second_item_failure_rolls_back_batch");
    stage = "seed_rerun";
    var notebookBefore = await Stock("notebook");
    await store.SeedAsync(ct);
    Check((await Stock("notebook")).Stock == notebookBefore.Stock && (await Stock(a)).Stock == 5 && await OrderCount(key1) == 1, "seed_rerun_preserves_stock_and_orders");
    // A NEW client proves DB persistence; separate HTTP two-phase probe proves process restart too.
    stage = "new_client_replay";
    var freshStore = new MongoOrderStore(new MongoClient(ClientFactory.Settings(config)), config, new NoopTransactionObserver());
    var freshReplay = await freshStore.PlaceAsync(key1, payload, ct);
    Check(freshReplay.Replayed && freshReplay.Order.OrderId == first.Order.OrderId, "new_client_persistent_idempotency");
    Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", auth = config.Auth, checksPassed = checks.Count, checks, races = stats,
        mutations = "synthetic fixtures retained; no reset/drop", processRestartProof = "requires tools/http_probe.py before + after" }));
}
catch (Exception ex)
{
    // Never print raw driver exception strings (which may contain destinations or credentials).
    Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", code = ex is ProbeFailure p ? p.Message : "live_probe_failed",
        exceptionType = ex.GetType().Name, serverCode = (ex as MongoCommandException)?.Code,
        serverCodeName = (ex as MongoCommandException)?.CodeName, stage,
        command = ex is MongoCommandException { Command.ElementCount: > 0 } commandError &&
            new[] { "create", "find", "insert", "update", "commitTransaction", "abortTransaction", "aggregate" }.Contains(commandError.Command.GetElement(0).Name)
            ? commandError.Command.GetElement(0).Name : null,
        checksPassed = checks.Count, checks }));
    Environment.ExitCode = 1;
}

sealed class ProbeFailure(string message) : Exception(message);
sealed class InjectedAbort : Exception;
sealed class FailAfterFirstReservation : ITransactionObserver
{
    public Task AfterReservationAsync(int completedReservations, CancellationToken cancellationToken) =>
        completedReservations == 1 ? throw new InjectedAbort() : Task.CompletedTask;
}
