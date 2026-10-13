using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderService;

public sealed record LineItem(string ProductId, int Quantity);
public sealed record OrderRequest(LineItem[]? Items);
public sealed record ValidatedOrder(LineItem[] Items, string Fingerprint);
public sealed record Product([property: BsonId] string Id, string Name, int Stock);
public sealed record Order([property: BsonId] string Id, string OrderId, string Fingerprint,
    LineItem[] Items, DateTime CreatedAtUtc);
public sealed record OrderResult(Order Order, bool Replayed);
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record ApiError(string Code, string RequestId);

public sealed class OrderException(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public static class OrderRules
{
    public static readonly Regex Identifier = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    public static void ValidateKey(string key)
    {
        if (!Identifier.IsMatch(key)) throw new OrderException("invalid_idempotency_key", 400);
    }
    public static ValidatedOrder Validate(OrderRequest? request)
    {
        if (request?.Items is not { Length: >= 1 and <= 8 } items || items.Any(i => i is null ||
                i.ProductId is null || !Identifier.IsMatch(i.ProductId) || i.Quantity is < 1 or > 100) ||
            items.Select(i => i.ProductId).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new OrderException("invalid_order", 400);
        var normalized = items.OrderBy(i => i.ProductId, StringComparer.Ordinal).ToArray();
        var canonical = "v1\n" + string.Join('\n', normalized.Select(i => $"{i.ProductId}:{i.Quantity}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(normalized, hash);
    }
    public static OrderResult Replay(Order order, ValidatedOrder request)
    {
        if (order.Fingerprint != request.Fingerprint) throw new OrderException("idempotency_conflict", 409);
        return new(order, true);
    }
    public static (int Limit, string? Cursor) Page(string? limit, string? cursor)
    {
        if (limit is not null && (!int.TryParse(limit, out var n) || n is < 1 or > 100) ||
            cursor is not null && !Identifier.IsMatch(cursor))
            throw new OrderException("invalid_pagination", 400);
        return (limit is null ? 25 : int.Parse(limit, System.Globalization.CultureInfo.InvariantCulture), cursor);
    }
}

public interface IOrderStore
{
    Task<OrderResult> PlaceAsync(string key, OrderRequest request, CancellationToken cancellationToken);
    Task<Page<Product>> InventoryAsync(int limit, string? cursor, CancellationToken cancellationToken);
    Task<Page<Order>> OrdersAsync(int limit, string? cursor, CancellationToken cancellationToken);
}

// Only the direct integration harness supplies a throwing implementation. No HTTP switch exists.
public interface ITransactionObserver
{
    Task AfterReservationAsync(int completedReservations, CancellationToken cancellationToken);
}
public sealed class NoopTransactionObserver : ITransactionObserver
{
    public Task AfterReservationAsync(int completedReservations, CancellationToken cancellationToken) => Task.CompletedTask;
}
