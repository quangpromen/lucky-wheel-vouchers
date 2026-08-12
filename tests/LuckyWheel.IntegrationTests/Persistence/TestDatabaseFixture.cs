using System;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LuckyWheel.IntegrationTests.Persistence;

public class TestDatabaseFixture : IDisposable
{
    private const string DevelopmentDatabaseName = "LuckyWheelDb";
    private const string IntegrationDatabaseName = "LuckyWheelDb_IntegrationTests";
    private static readonly object _lock = new();
    private static bool _databaseInitialized;

    public string ConnectionString { get; }

    public TestDatabaseFixture()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("8960ad78-d5c4-4378-8350-7ec592132261")
            .AddEnvironmentVariables()
            .Build();

        var baseConn = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(baseConn))
        {
            throw new InvalidOperationException("DefaultConnection connection string is missing from User Secrets or Environment Variables.");
        }

        var connectionBuilder = new SqlConnectionStringBuilder(baseConn);
        if (!string.Equals(connectionBuilder.InitialCatalog, DevelopmentDatabaseName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(connectionBuilder.InitialCatalog, IntegrationDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Integration tests only accept '{DevelopmentDatabaseName}' or '{IntegrationDatabaseName}' as the configured database name.");
        }

        connectionBuilder.InitialCatalog = IntegrationDatabaseName;
        ConnectionString = connectionBuilder.ConnectionString;

        lock (_lock)
        {
            if (!_databaseInitialized)
            {
                using var context = CreateContext();
                context.Database.Migrate();
                _databaseInitialized = true;
            }
        }
    }

    public ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new ApplicationDbContext(options);
    }

    public void Dispose()
    {
        // Cleanup resources if necessary
    }
}

[CollectionDefinition("DatabaseCollection")]
public class DatabaseCollection : ICollectionFixture<TestDatabaseFixture>
{
}
