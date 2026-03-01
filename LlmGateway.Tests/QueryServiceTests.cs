using LlmGateway.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LlmGateway.Tests;

public class QueryServiceTests
{
    // ── CosmosQueryService ────────────────────────────────────────────────────

    // CosmosClient fake-yhteysjonolla — ei oikeasti yhdistä ennen kyselyä.
    // Testaa validointisuojan joka ajetaan ennen Cosmos-kutsua.
    private static CosmosQueryService CreateCosmosService() =>
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
    public async Task Cosmos_SelectQuery_DoesNotThrowOnValidation()
    {
        var service = CreateCosmosService();
        // Heittää Cosmos-tasolla (ei oikeaa palvelinta), mutta EI InvalidOperationException
        // — validointi menee läpi.
        var ex = await Record.ExceptionAsync(() =>
            service.ExecuteQueryAsync("SELECT c.id FROM c"));

        Assert.IsNotType<InvalidOperationException>(ex);
    }

    [Theory]
    [InlineData("DELETE FROM c")]
    [InlineData("UPDATE c SET c.x = 1")]
    [InlineData("DROP CONTAINER c")]
    [InlineData("  delete from c")]
    public async Task Cosmos_NonSelectQuery_ThrowsInvalidOperationException(string sql)
    {
        var service = CreateCosmosService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteQueryAsync(sql));
    }

    [Fact]
    public async Task Cosmos_EmptySql_ThrowsArgumentException()
    {
        var service = CreateCosmosService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteQueryAsync(""));
    }

    [Fact]
    public async Task Cosmos_WhitespaceSql_ThrowsArgumentException()
    {
        var service = CreateCosmosService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteQueryAsync("   "));
    }

    // ── SqlQueryService ───────────────────────────────────────────────────────

    // SqlQueryService fake-yhteysjonolla — epäkelpo yhteysjono kaatuu SqlConnection.OpenAsync:ssa,
    // mutta validointisuoja ajetaan ennen sitä.
    private static SqlQueryService CreateSqlService() =>
        new SqlQueryService(
            Options.Create(new SqlOptions { ConnectionString = "Server=fake;Database=fake;" }),
            NullLogger<SqlQueryService>.Instance);

    [Fact]
    public async Task Sql_SelectQuery_DoesNotThrowOnValidation()
    {
        var service = CreateSqlService();
        // Heittää SqlException/SocketException (ei oikeaa palvelinta), mutta EI InvalidOperationException
        // — validointi menee läpi.
        var ex = await Record.ExceptionAsync(() =>
            service.ExecuteQueryAsync("SELECT * FROM mittaukset"));

        Assert.IsNotType<InvalidOperationException>(ex);
    }

    [Theory]
    [InlineData("DELETE FROM mittaukset")]
    [InlineData("UPDATE mittaukset SET lampotila = 0")]
    [InlineData("DROP TABLE mittaukset")]
    [InlineData("  insert into mittaukset values (1)")]
    public async Task Sql_NonSelectQuery_ThrowsInvalidOperationException(string sql)
    {
        var service = CreateSqlService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteQueryAsync(sql));
    }

    [Fact]
    public async Task Sql_EmptySql_ThrowsArgumentException()
    {
        var service = CreateSqlService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteQueryAsync(""));
    }

    [Fact]
    public async Task Sql_WhitespaceSql_ThrowsArgumentException()
    {
        var service = CreateSqlService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteQueryAsync("   "));
    }
}
