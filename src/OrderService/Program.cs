using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;
using OrderService;

try
{
    var config = AppConfig.FromEnvironment();
    config.Validate();
    if (args is ["seed"])
    {
        config.DemandWrites();
        var store = new MongoOrderStore(new MongoClient(ClientFactory.Settings(config)), config, new NoopTransactionObserver());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await store.SeedAsync(timeout.Token);
        Console.WriteLine("{\"status\":\"passed\",\"command\":\"seed\",\"preservesExistingStock\":true}");
        return;
    }
    var builder = WebApplication.CreateBuilder(args);
    // Deliberately operator-local only: do not inherit ASPNETCORE_URLS from an unsafe environment.
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
    builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8192);
    builder.Logging.ClearProviders(); // Never print connection strings, driver exceptions or request payloads.
    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(ClientFactory.Settings(config)));
    builder.Services.AddSingleton<ITransactionObserver, NoopTransactionObserver>();
    builder.Services.AddSingleton<IOrderStore, MongoOrderStore>();
    var app = builder.Build();
    app.Use(async (context, next) =>
    {
        var requestId = Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Request-Id"] = requestId;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                var supplied = context.Request.Headers.Authorization.ToString();
                var expected = "Bearer " + config.ApiToken;
                if (config.ApiToken.Length < 32 || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
                    throw new OrderException("unauthorized", 401);
                // No cookie authentication / CORS. Reject cross-origin browser mutations as defense in depth.
                if (context.Request.Method != "GET" && context.Request.Headers.Origin.Count != 0 &&
                    context.Request.Headers.Origin.ToString() != $"{context.Request.Scheme}://{context.Request.Host}")
                    throw new OrderException("cross_origin_forbidden", 403);
            }
            await next();
        }
        catch (OrderException ex) { await Error(ex.Status, ex.Code); }
        catch (BadHttpRequestException ex) { await Error(ex.StatusCode == 413 ? 413 : 400, "invalid_json"); }
        catch (JsonException) { await Error(400, "invalid_json"); }
        catch (ConfigurationException) { await Error(403, "writes_disabled"); }
        catch (OperationCanceledException) { if (!context.RequestAborted.IsCancellationRequested) await Error(503, "outcome_unknown_retry_same_key"); }
        catch (MongoException) { await Error(503, "database_unavailable_retry_same_key"); }
        catch (Exception) { await Error(500, "internal_error"); }

        async Task Error(int status, string code)
        {
            if (context.Response.HasStarted) return;
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ApiError(code, requestId));
        }
    });
    app.UseDefaultFiles();
    app.UseStaticFiles();
    var instanceId = Guid.NewGuid().ToString("N");
    app.MapGet("/health", () => Results.Ok(new { status = "alive", dependencyCheck = false, instanceId }));
    app.MapGet("/api/inventory", async (HttpRequest request, IOrderStore store, CancellationToken ct) =>
    {
        var page = OrderRules.Page(request.Query["limit"].FirstOrDefault(), request.Query["cursor"].FirstOrDefault());
        return Results.Ok(await store.InventoryAsync(page.Limit, page.Cursor, ct));
    });
    app.MapGet("/api/orders", async (HttpRequest request, IOrderStore store, CancellationToken ct) =>
    {
        var page = OrderRules.Page(request.Query["limit"].FirstOrDefault(), request.Query["cursor"].FirstOrDefault());
        return Results.Ok(await store.OrdersAsync(page.Limit, page.Cursor, ct));
    });
    app.MapPost("/api/orders", async (HttpRequest request, IOrderStore store, CancellationToken ct) =>
    {
        if (!request.HasJsonContentType()) throw new OrderException("json_required", 415);
        var keys = request.Headers["Idempotency-Key"];
        if (keys.Count != 1) throw new OrderException("invalid_idempotency_key", 400);
        var key = keys[0] ?? "";
        OrderRules.ValidateKey(key);
        // Explicit read ensures JSON binding errors pass through the stable error middleware.
        var body = await request.ReadFromJsonAsync<OrderRequest>(cancellationToken: ct);
        OrderRules.Validate(body);
        var result = await store.PlaceAsync(key, body!, ct);
        return Results.Json(result, statusCode: result.Replayed ? 200 : 201);
    });
    await app.RunAsync();
}
catch (Exception)
{
    // Startup/CLI failure is intentionally sanitized. Do not echo credentials or raw driver errors.
    Console.Error.WriteLine("{\"status\":\"failed\",\"code\":\"startup_or_seed_failed\"}");
    Environment.ExitCode = 1;
}

public partial class Program;
