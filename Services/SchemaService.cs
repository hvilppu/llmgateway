namespace LlmGateway.Services;

// Palauttaa tietokannan skeeman kovakoodattuna merkkijonona.
// Skeema on sovellustason tietoa — ei haeta tietokannasta eikä aseteta appsettingsiin.
public interface ISchemaProvider
{
    Task<string> GetSchemaAsync(CancellationToken cancellationToken = default);
}

// Cosmos DB -skeema: documents-containerin kenttärakenne.
public class CosmosSchemaProvider : ISchemaProvider
{
    private const string Schema =
        """
        Container: documents
          c.id (string)
          c.content.paikkakunta (string)
          c.content.pvm (string)
          c.content.lampotila (number)
        """;

    public Task<string> GetSchemaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Schema);
}

// MS SQL -skeema: mittaukset-taulun kenttärakenne.
public class SqlSchemaProvider : ISchemaProvider
{
    private const string Schema =
        """
        Table: mittaukset
          id (int)
          paikkakunta (nvarchar)
          pvm (date)
          lampotila (float)
        """;

    public Task<string> GetSchemaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Schema);
}
