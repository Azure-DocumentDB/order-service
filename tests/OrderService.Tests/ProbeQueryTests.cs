using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using OrderService;
using Xunit;

namespace OrderService.Tests;

public class ProbeQueryTests
{
    private static readonly RenderArgs<Order> Render = new(
        BsonSerializer.SerializerRegistry.GetSerializer<Order>(), BsonSerializer.SerializerRegistry);

    [Fact]
    public void AnyEqOnTraversedScalarProductIdReproducesSerializerFailureOffline()
    {
        // AnyEq requires the resolved FIELD to have an array serializer. After traversing Items,
        // ProductId resolves to a string serializer, even though the query crosses an array.
        var oldFilter = Builders<Order>.Filter.AnyEq("Items.ProductId", "synthetic-product");
        Assert.Throws<InvalidOperationException>(() => oldFilter.Render(Render));
    }

    [Fact]
    public void TypedElemMatchRendersExpectedProductMembershipQuery()
    {
        var product = "synthetic-product";
        var filter = Builders<Order>.Filter.ElemMatch(o => o.Items, item => item.ProductId == product);
        var rendered = filter.Render(Render);
        Assert.Equal(BsonDocument.Parse("{ Items: { $elemMatch: { ProductId: 'synthetic-product' } } }"), rendered);
    }
}
