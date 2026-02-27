using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LlmGateway.Tests;

public class QueryServiceTests
{
    // CosmosClient with fake connection string — won't actually connect until a query is made.
    // This lets us test the SQL validation guard that runs before any Cosmos call.
    private static CosmosQueryService CreateService() =>
        new CosmosQueryService(
            new CosmosClient("AccountEndpoint=https://fake.documents.azure.com:443/;AccountKey=dGVzdA==;"),
            Options.Create(new CosmosRagOptions
            {
                ConnectionString = "fake",
                DatabaseName = "testdb",
                ContainerName = "testcontainer"
            }),
            NullLogger<CosmosQueryService>.Instance);

    [Fact]
    public async Task ExecuteQuery_SelectQuery_DoesNotThrowOnValidation()
    {
        var service = CreateService();
        // Will throw at Cosmos level (no real server), but should NOT throw InvalidOperationException
        // for the SQL validation guard. We catch only InvalidOperationException to verify validation passes.
        var ex = await Record.ExceptionAsync(() =>
            service.ExecuteQueryAsync("SELECT c.id FROM c"));

        Assert.IsNotType<InvalidOperationException>(ex);
    }

    [Theory]
    [InlineData("DELETE FROM c")]
    [InlineData("UPDATE c SET c.x = 1")]
    [InlineData("DROP CONTAINER c")]
    [InlineData("  delete from c")] // leading whitespace + lowercase
    public async Task ExecuteQuery_NonSelectQuery_ThrowsInvalidOperationException(string sql)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteQueryAsync(sql));
    }

    [Fact]
    public async Task ExecuteQuery_EmptySql_ThrowsArgumentException()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteQueryAsync(""));
    }

    [Fact]
    public async Task ExecuteQuery_WhitespaceSql_ThrowsArgumentException()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteQueryAsync("   "));
    }
}
